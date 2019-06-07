// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;


namespace Microsoft.AspNetCore.Mvc.Infrastructure
{
    /// <summary>
    /// Executes an <see cref="ObjectResult"/> to write to the response.
    /// </summary>
    public class ObjectResultExecutor : IActionResultExecutor<ObjectResult>
    {
        private delegate Task<(Type, IList)> ConvertAsyncEnumerable(object value);
        private static readonly Task<(Type, IList)> NullResult = Task.FromResult<(Type, IList)>(default);

        private static readonly MethodInfo Converter = typeof(ObjectResultExecutor).GetMethod(nameof(ReadAsyncEnumerable), BindingFlags.NonPublic | BindingFlags.Static);

        private readonly ConcurrentDictionary<Type, ConvertAsyncEnumerable> _asyncEnumerableConverters =
            new ConcurrentDictionary<Type, ConvertAsyncEnumerable>();

        /// <summary>
        /// Creates a new <see cref="ObjectResultExecutor"/>.
        /// </summary>
        /// <param name="formatterSelector">The <see cref="OutputFormatterSelector"/>.</param>
        /// <param name="writerFactory">The <see cref="IHttpResponseStreamWriterFactory"/>.</param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
        public ObjectResultExecutor(
            OutputFormatterSelector formatterSelector,
            IHttpResponseStreamWriterFactory writerFactory,
            ILoggerFactory loggerFactory)
        {
            if (formatterSelector == null)
            {
                throw new ArgumentNullException(nameof(formatterSelector));
            }

            if (writerFactory == null)
            {
                throw new ArgumentNullException(nameof(writerFactory));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            FormatterSelector = formatterSelector;
            WriterFactory = writerFactory.CreateWriter;
            Logger = loggerFactory.CreateLogger<ObjectResultExecutor>();
        }

        /// <summary>
        /// Gets the <see cref="ILogger"/>.
        /// </summary>
        protected ILogger Logger { get; }

        /// <summary>
        /// Gets the <see cref="OutputFormatterSelector"/>.
        /// </summary>
        protected OutputFormatterSelector FormatterSelector { get; }

        /// <summary>
        /// Gets the writer factory delegate.
        /// </summary>
        protected Func<Stream, Encoding, TextWriter> WriterFactory { get; }

        /// <summary>
        /// Executes the <see cref="ObjectResult"/>.
        /// </summary>
        /// <param name="context">The <see cref="ActionContext"/> for the current request.</param>
        /// <param name="result">The <see cref="ObjectResult"/>.</param>
        /// <returns>
        /// A <see cref="Task"/> which will complete once the <see cref="ObjectResult"/> is written to the response.
        /// </returns>
        public virtual Task ExecuteAsync(ActionContext context, ObjectResult result)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            InferContentTypes(context, result);

            var objectType = result.DeclaredType;

            if (objectType == null || objectType == typeof(object))
            {
                objectType = result.Value?.GetType();
            }

            var value = result.Value;

            if (value != null)
            {
                var unwrapAsyncEnumerable = UnwrapAsyncEnumerable(result.Value);
                if (ReferenceEquals(unwrapAsyncEnumerable, NullResult))
                {
                    // This isn't an IAsyncEnumerable. Nothing to do here.
                }
                else if (unwrapAsyncEnumerable.IsCompletedSuccessfully)
                {
                    var asyncEnumerableResult = unwrapAsyncEnumerable.Result;
                    objectType = asyncEnumerableResult.objectType;
                    value = asyncEnumerableResult.enumeratedList;
                }
                else
                {
                    return ExecuteAsyncAwaited(context, result, unwrapAsyncEnumerable);
                }
            }

            return ExecuteAsyncCore(context, result, objectType, value);
        }

        private async Task ExecuteAsyncAwaited(ActionContext context, ObjectResult result, Task<(Type, IList)> task)
        {
            var (objectType, value) = await task;
            await ExecuteAsyncCore(context, result, objectType, value);
        }

        private Task ExecuteAsyncCore(ActionContext context, ObjectResult result, Type objectType, object value)
        {
            var formatterContext = new OutputFormatterWriteContext(
                context.HttpContext,
                WriterFactory,
                objectType,
                value);

            var selectedFormatter = FormatterSelector.SelectFormatter(
                formatterContext,
                (IList<IOutputFormatter>)result.Formatters ?? Array.Empty<IOutputFormatter>(),
                result.ContentTypes);
            if (selectedFormatter == null)
            {
                // No formatter supports this.
                Logger.NoFormatter(formatterContext);

                context.HttpContext.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                return Task.CompletedTask;
            }

            Logger.ObjectResultExecuting(result.Value);

            result.OnFormatting(context);
            return selectedFormatter.WriteAsync(formatterContext);
        }

        private static void InferContentTypes(ActionContext context, ObjectResult result)
        {
            Debug.Assert(result.ContentTypes != null);
            if (result.ContentTypes.Count != 0)
            {
                return;
            }

            // If the user sets the content type both on the ObjectResult (example: by Produces) and Response object,
            // then the one set on ObjectResult takes precedence over the Response object
            var responseContentType = context.HttpContext.Response.ContentType;
            if (!string.IsNullOrEmpty(responseContentType))
            {
                result.ContentTypes.Add(responseContentType);
            }
            else if (result.Value is ProblemDetails)
            {
                result.ContentTypes.Add("application/problem+json");
                result.ContentTypes.Add("application/problem+xml");
            }
        }

        private Task<(Type objectType, IList enumeratedList)> UnwrapAsyncEnumerable(object value)
        {
            var type = value.GetType();
            if (!_asyncEnumerableConverters.TryGetValue(type, out var result))
            {
                var enumerableType = ClosedGenericMatcher.ExtractGenericInterface(type, typeof(IAsyncEnumerable<>));
                result = null;
                if (enumerableType != null)
                {
                    var enumeratedObjectType = enumerableType.GetGenericArguments()[0];

                    var converter = (ConvertAsyncEnumerable)Converter
                        .MakeGenericMethod(enumeratedObjectType)
                        .CreateDelegate(typeof(ConvertAsyncEnumerable));

                    _asyncEnumerableConverters[type] = converter;
                    result = converter;
                }
            }

            if (result is null)
            {
                return NullResult;
            }

            return result(value);
        }

        private static async Task<(Type, IList)> ReadAsyncEnumerable<T>(object asyncEnumerable)
        {
            var converted = (IAsyncEnumerable<T>)asyncEnumerable;
            var result = new List<T>();

            await foreach (var item in converted)
            {
                result.Add(item);
            }

            return (result.GetType(), result);
        }
    }
}
