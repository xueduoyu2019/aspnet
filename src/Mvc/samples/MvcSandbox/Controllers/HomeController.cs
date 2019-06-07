// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MvcSandbox.Controllers
{
    public class HomeController : Controller
    {
        private readonly AppDbContext dbContext;

        public HomeController(AppDbContext appDbContext)
        {
            this.dbContext = appDbContext;
            this.dbContext.Persons.Add(new Person
            {
                Id = 1,
                Name = "Person1",
            });

            this.dbContext.Persons.Add(new Person
            {
                Id = 2,
                Name = "Person2",
            });
        }

        [ModelBinder]
        public string Id { get; set; }

        public IAsyncEnumerable<Person> Index()
        {
            return this.dbContext.Persons;
        }
    }

    public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }


        public DbSet<Person> Persons { get; set; }
    }

    public class Person
    {
        public int Id { get; set; }

        public string Name { get; set; }
    }
}
