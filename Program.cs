using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ef_core_implicit_transaction
{
    class Program
    {
        static void Main(string[] args)
        {
            // Create the initial user
            using (var appDbContext = new AppDbContext())
            {
                appDbContext.Database.EnsureDeleted();
                appDbContext.Database.EnsureCreated();

                var tagA = appDbContext.Tags.Add(new Tag { Name = "Tag A" }).Entity;
                var tagB = appDbContext.Tags.Add(new Tag { Name = "Tag B" }).Entity;
                var tagC = appDbContext.Tags.Add(new Tag { Name = "Tag C" }).Entity;

                var group1 = appDbContext.Groups.Add(new Group { Name = "Group 1" }).Entity;
                var group2 = appDbContext.Groups.Add(new Group { Name = "Group 2" }).Entity;
                var group3 = appDbContext.Groups.Add(new Group { Name = "Group 3" }).Entity;

                appDbContext.Users.Add(new User {
                    Tags = new[] { new UserTag { Tag = tagA }, new UserTag { Tag = tagB } },
                    Groups = new[] { new UserGroup { Group = group1 } }
                });

                appDbContext.SaveChanges();
            }

            // Apply the update
            using (var appDbContext = new AppDbContext())
            {
                var user = appDbContext.Users
                    .Include(u => u.Tags)
                    .Include(u => u.Groups)
                    .Single();

                // This won't work, because EF Core expects to affect exactly one entity with this (not 0-N)
                //appDbContext.Entry(new UserTag { UserId = user.Id }).State = EntityState.Deleted;
                //appDbContext.Entry(new UserGroup { UserId = user.Id }).State = EntityState.Deleted;

                // This works, it is probably the best way when not wanting to use `Entry`, `ExecuteSql` or EF Core Plus
                //user.Tags.Clear();
                //user.Groups.Clear();
                //user.Tags.Add(new UserTag { TagId = 3 /* Tag C */ });
                //user.Groups.Add(new UserGroup { GroupId = 1 /* Group 1 */ });
                //user.Groups.Add(new UserGroup { GroupId = 2 /* Group 2 */ });

                // This works and it even persists in the database, but it throws locally :-D
                user.Tags = new[] { new UserTag { TagId = 3 /* Tag C */ } };
                user.Groups = new[] { new UserGroup { GroupId = 1 /* Group 1 */ }, new UserGroup { GroupId = 2 /* Group 2 */ } };

                try
                {
                    appDbContext.SaveChanges();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    // Ignore update problem, EF Core rollback to the rescue!
                }
            }

            // Report the actual data
            using (var appDbContext = new AppDbContext())
            {
                var user = appDbContext.Users
                    .AsNoTracking() /// Ensure we're not seeing just changes stored locally in change tracker and not remotely in the DB
                    .Include(u => u.Tags).ThenInclude(ut => ut.Tag)
                    .Include(u => u.Groups).ThenInclude(ug =>  ug.Group)
                    .Single();

                Console.WriteLine($"Tags: {string.Join(", ", user.Tags.Select(ut => ut.TagId))} (expected 3)");
                Console.WriteLine($"Groups: {string.Join(", ", user.Groups.Select(ug => ug.GroupId))} (exptected 1, 2)");
            }
        }
    }

    public class AppDbContext: DbContext
    {
        public DbSet<User> Users { get; set; }
        public DbSet<Tag> Tags { get; set; }
        public DbSet<UserTag> UserTags { get; set; }
        public DbSet<Group> Groups { get; set; }
        public DbSet<UserGroup> UserGroups { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder
                .EnableSensitiveDataLogging()
                // TODO: Find a way to use ILoggingBuilder in a console application
                #pragma warning disable CS0618
                .UseLoggerFactory(new LoggerFactory().AddConsole())
                #pragma warning restore CS0618
                .UseSqlServer($@"Server=(localdb)\{nameof(ef_core_implicit_transaction)};Database={nameof(ef_core_implicit_transaction)};");
        }
    }

    public class User
    {
        public long Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public ICollection<UserTag> Tags { get; set; }
        public ICollection<UserGroup> Groups { get; set; }
    }

    public class Tag
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public ICollection<User> Users { get; set; }
    }

    public class UserTag
    {
        public long Id { get; set; }
        public long UserId { get; set; }
        public User User { get; set; }
        public long TagId { get; set; }
        public Tag Tag { get; set; }
    }

    public class Group
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public ICollection<User> Users { get; set; }
    }

    public class UserGroup
    {
        public long Id { get; set; }
        public long UserId { get; set; }
        public User User { get; set; }
        public long GroupId { get; set; }
        public Group Group { get; set; }
    }
}
