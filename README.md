# EF Core implicit transaction

This is a spike to find out if EF Core has implicit database transaction around `using (var context = new AppDbContext()) { … }`
and whether its rollback behavior is what I need in a case where associated entities get deleted and new ones inserted.

I am starting by creating a new .NET Core console application:

```powershell
dotnet new console
```

The next step is to pull the `Microsoft.EntityFrameworkCore.SqlServer` NuGet package so that we can work with EF Core and the
SQL Server provider which we will use LocalDB through.

```powershell
dotnet add package Microsoft.EntityFrameworkCore.SqlServer
```

At this point we are ready to create the LocalDB instance. We will make it match the spike name, but will replace
the dashes with underscores like .NET Core does when scaffolding the `console` template's namespace.
That way we can use `nameof` instead of hardcoding the database name in the code and it will stay in sync with the spike name.

```powershell
sqllocaldb create ef_core_implicit_transaction -s
```

We will not be using migrations in this project, so we don't need the `dotnet ef` tools.
They won't be accessible before we install the `Microsoft.EntityFrameworkCore.Design` NuGet package, which we will skip for now.

At this point we're ready to go ahead and setup the DB context and model classes. Let's start with the model.

In this spike we are looking to find out, if with complex update logic, such as a user being updated
by a model which brings in IDs of associated entities to keep on the new user, the EF Core default implicit transaction
rollback will ensure the correct previous state of the database is restored in case the update fails.

The update is a collection if multiple queries, some destructive, so it's important that all of that can be rolled back
in case any individual queries fail. Otherwise the updated user entity would be in an inconsistent state after borked update.
This could bring our whole app down.

Here's what the data model could look like:

```csharp
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
```

We can now start watching out project and fixing little things like formatting and namespaces.

```powershell
dotnet watch run
```

VS Code will prompt you to add "required assets", do so in order to get debugging configuration etc.
If you miss that prompt, restart VS Code and it should come up again.
You can do so quickly with F1 > Reload window.

In case you run into a problem where OmniSharp reports that all classes are present already in the namespace,
go to F1 > OmniSharp: Restart OmniSharp and it should fix itself.

Now that we have our data model defined, we can start configuring the DB context class for EF.

```csharp
public class AppDbContext: DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<Tag> Tags { get; set; }
    public DbSet<UserTag> UserTags { get; set; }
    public DbSet<Group> Groups { get; set; }
    public DbSet<UserGroup> UserGroups { get; set; }
}
```

We can go ahead and set up the model scenario now.

```csharp
static void Main(string[] args)
{
    // Create the initial user
    using (var appDbContext = new AppDbContext())
    {
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
    // TODO
}
```

Our `dotnet watch run` will keep failing now as we are not connected to the LocalDB instance
we've created previously.

We will change that by overriding `OnConfiguring` on `DbContext`:

```csharp
protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
{
    optionsBuilder.UseSqlServer($@"Server=(localdb)\{nameof(ef_core_implicit_transaction)};Database={nameof(ef_core_implicit_transaction)};");
}
```

You might need to add `Trusted_Connection=True;` to force the use of Windows credentials to log in
but I find it generally to work without it.

We get a database connection error now:
*Cannot open database "ef_core_implicit_transaction" requested by the login. The login failed.*

We could fix that by creating the database manually by connecting to
`(localdb)\ef_core_implicit_transaction` or using the EF tools had we installed the above
mentioned `Microsoft.EntityFrameworkCore.Design` NuGet package making the `dotnet ef` tools available.
Just creating the database is not enough, though, because then the tables would be missing,
so we will use a way which makes EF Core do all the work. In `Main`, add:

```csharp
static void Main(string[] args)
{
    // Create the initial user
    using (var appDbContext = new AppDbContext())
    {
        appDbContext.Database.EnsureCreated();
        …
```

We do not need to set up or use migrations at all, we will just use the inferred convention
based code first model. Since we are *not* using migrations here, we do not need to call `Migrate`.

Now `dotnet run` will successfull connect to the database at last.

For a spike like this, we want reproducible results, so we will extend the above snippet
by also deleting the database if it already exits so that we are always recreating an empty
database with just schema and no data.

```csharp
static void Main(string[] args)
{
    // Create the initial user
    using (var appDbContext = new AppDbContext())
    {
        appDbContext.Database.EnsureDeleted();
        appDbContext.Database.EnsureCreated();
        …
```

Before we move onto proving the conjecture we aim to work through with this spike, we'll
do one last enhancement, which is to log SQL commands as they are dispatched by EF Core.
We need the `Microsoft.Extensions.Logging.Console` NuGet package for that.

```powershell
dotnet add package Microsoft.Extensions.Logging.Console
```

Let's extend `OnConfiguring` method now:

```csharp
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
```

We enable sensitive data logging to see the values of parameters issued for the compiled SQL
commands and we use the `ConsoleLoggerProvider` in order to emit info and higher level messages.
(This includes the SQL commands themselves which are *Info* level.)

This method is obsolete, but I do not know how to use `ILoggingBuilder` in a console application,
so I am going to silence the warning instead until I'm forced to figure it out.

Now we're ready to start working on our spike. We've already set up the demo data, now we need to model
the situation that we're interested in observing.

For the user we set up, we will remove all their associated tags and groups and replace them
with new ones. These new ones might even be the same, but we've looking at a very simple update
mechanism here which doesn't complicate itself by diffing changes to the entity etc.
Instead, it takes everything away and replaces it with new values from a model based on the
old values which may have been not at all, partially or fully changed. We don't need to know.

Let's update `Main` some more:

```csharp
static void Main(string[] args)
{
    …
    // Apply the update
    using (var appDbContext = new AppDbContext())
    {
        var user = appDbContext.Users.Single();
        user.Tags = new[] { new UserTag { TagId = 3 /* Tag C */ } };
        user.Groups = new[] { new UserGroup { GroupId = 1 /* Group 1 */ }, new UserGroup { GroupId = 2 /* Group 2 */ } };
        appDbContext.SaveChanges();
    }
    …
```

This update is a good example, because it remove some things, leave some things and add some things, too.
The command goes through, but we also need to see if the user is updated correctly at the end.
We'll add another block after the one we just added and write some reporting logic then.

We'll also add a new package dependency for `Newtonsoft.Json` first to be able to serialize for inspection:

```powershell
dotnet add package Newtonsoft.Json
```

```csharp
static void Main(string[] args)
{
    …
    // Report the actual data
    using (var appDbContext = new AppDbContext())
    {
        var user = appDbContext.Users
            .Include(u => u.Tags).ThenInclude(ut => ut.Tag)
            .Include(u => u.Groups).ThenInclude(ug =>  ug.Group)
            .Single();

        var json = JsonConvert.SerializeObject(user, new JsonSerializerSettings {
            Formatting = Formatting.Indented,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        });

        Console.WriteLine(json);
    }
    …
```

Unfortunately, this change only adds items to user instead of replacing the existing items!
That's because we didn't materialize and remove the existing associated entities (tags, groups).
This is a known limitation of EF Core (and EF in general).

There are solutions to this, such as EF Core Plus, or writing SQL manually.
EF Core Plus unfortunately ties one to the SQL Server provider, so it compromises the promise of
EF Core a little bit, but we'll bite the bullet as we're using SQL Server anway and we
do not want the maintenance burden of concatenating SQL commands ourselves.
(Even though `ExecuteSql` is smart enough to convert template string literal substitutions to parameters!)

We could also drop down a level of abstraction and use Dapper or ADO .NET directly.
For now we'll stick with EF and go with EF Core Plus to solve this.

```powershell
dotnet add package Z.EntityFramework.Plus.EFCore
```

We can now first delete existing associated data and then replace it:

```csharp
static void Main(string[] args)
{
    …
    // Apply the update
    using (var appDbContext = new AppDbContext())
    {
        var user = appDbContext.Users.Single();
        appDbContext.UserTags.Where(ut => ut.UserId == user.Id).Delete();
        user.Tags = new[] { new UserTag { TagId = 3 /* Tag C */ } };
        appDbContext.UserGroups.Where(ug => ug.UserId == user.Id).Delete();
        user.Groups = new[] { new UserGroup { GroupId = 1 /* Group 1 */ }, new UserGroup { GroupId = 2 /* Group 2 */ } };
        appDbContext.SaveChanges();
    }
```

When we rerun `dotnet run` we can see the code works correctly now.

But what about when it doesn't?
Let's introduce a simulated failure while persisting the changes in `SaveChanges`,
we can do that for example by multiplying all the above harcoded IDs by 10,
making the references invalid.

`INSERT statement conflicted with the FOREIGN KEY constraint "FK_UserX_Xs_XId".`
`The conflict occurred in database "ef_core_implicit_transaction", table "dbo.Xs", column 'Id'.`

`UserGroups` and `UserTags` are empty now!
But that's because EF Core Plus issues its own SQL command and maybe those do not fall under the
DbContext default transaction? Let's revert the hardcoded IDs change and try another way.

What if instead we remove the associations the EF way: materializing them first and then removing them.
We will remove the EF Core NuGet package reference and use `Include` to pull the actual
`UserTag` and `UserGroup` instances in. We will them remove those.

```csharp
static void Main(string[] args)
{
    …
    // Apply the update
    using (var appDbContext = new AppDbContext())
    {
        var user = appDbContext.Users
            .Include(u => u.Tags)
            .Include(u => u.Groups)
            .Single();

        user.Tags.Clear();
        user.Groups.Clear();

        user.Tags.Add(new UserTag { TagId = 3 /* Tag C */ });
        user.Groups.Add(new UserGroup { GroupId = 1 /* Group 1 */ });
        user.Groups.Add(new UserGroup { GroupId = 2 /* Group 2 */ });

        appDbContext.SaveChanges();
    }
```

Now the original entities are removed and the new ones are placed in.
The important thing is that the entities are not left hanging around,
but are actually deleted. This is a new behavior in EF Core called
deleting orphans, in EF6 the orphaned entities would have stuck around.

More on the topic in [Delete orphans examples](https://docs.microsoft.com/en-us/ef/core/saving/cascade-delete#delete-orphans-examples).

Lets put the invalid IDs back in in order to cause an insertion failure once again.
We are interested in finding out whether the original associated entities will be restored.

If we follow the log of SQL statements printed out to the console,
we can see that indeed delete statements were issued for the old associations:

```sql
DELETE FROM [UserTags] WHERE [Id] = @p5 -- @p5=1
DELETE FROM [UserTags] WHERE [Id] = @p6 -- @p6=2
INSERT INTO [UserTags] ([TagId], [UserId]) VALUES (@p7, @p8) -- @p7=30 @p8=1
```

We don't even get past this failure on `UserTags` so the `UserGroup` query never happens.

Now we can now wrap the `SaveChanges` for this block in a `try`-`catch` block so that
the report block happens regardless of whether the update succeeds or not.

```csharp
try
{
    appDbContext.SaveChanges();
}
catch
{
    // Ignore update problem, EF Core rollback to the rescue!
}
```

The final verdict is: EF Core won't lose our old, removed entities and the
implicit transaction will indeed save us.

That means we don't have to start a nested transaction here,
it will be redundant when we remove the entities the EF way by materializing them first
and it won't save us if we issue the delete statements out of band of EF,
because those will not be a part of that transaction.

BTW in case you were wondering if we could perhaps use the good old
`context.Entry(new UserX { UserId = user.Id }).State = EntityState.Deleted`,
then unfortunately, that's not a stand in for
`context.UserXs.Where(x => x.UserId == user.Id).Delete()`
from EF Core Plus.

The reason for that is that EF Core expects that call to only affect one entity,
if it affects zero (because no associations existed) or more (multiple tags/groups),
EF will freak out, as it should, because we're talking an `Entry`, signular.

We could dispatch the above call for all IDs of the associated entities,
but then how do you get the IDs? From the update model, we only have the new ones.
Do you go to the database to fetch those IDs? Well then you might as well materialize,
the roundtrip has been wasted already and the difference between a set of IDs and a three
numerical column joining table is not significant in terms of memory consumed.

Basically you just get ugly code and it even won't save you from the roundtrip.
Might as well materialize.

## Another Thought

Let's get rid of the JSON reporting, it's too messy. We really just care about the IDs.

```powershell
dotnet remove package Newtonsoft.Json
```

```csharp
static void Main(string[] args)
{
    …

    // Report the actual data
    using (var appDbContext = new AppDbContext())
    {
        var user = appDbContext.Users
            .Include(u => u.Tags).ThenInclude(ut => ut.Tag)
            .Include(u => u.Groups).ThenInclude(ug =>  ug.Group)
            .Single();

        Console.WriteLine($"Tags: {string.Join(", ", user.Tags.Select(ut => ut.TagId))} (expected 3)");
        Console.WriteLine($"Groups: {string.Join(", ", user.Groups.Select(ug => ug.GroupId))} (exptected 1, 2)");
    }
```

What will happen if we replace the collection instead of calling `Clear`?

```csharp
user.Tags = new[] { new UserTag { TagId = 3 /* Tag C */ } };
user.Groups = new[] { new UserGroup { GroupId = 1 /* Group 1 */ }, new UserGroup { GroupId = 2 /* Group 2 */ } };
```

## Stored Procedures

EF Core executes stored procs by using `ExecuteSql` which means this would not work with in memory and the SQL would not
be provider agnostic. Ideally we'd like to find a way that works across all of ADO .NET.

## To-Do

### Find out if Dapper bulk delete works

Dapper has bulk delete: https://dapper-tutorial.net/bulk-delete

It's not clear whether it allows skipping materialization for this. The delete many example makes it look like it does,
but it might still materialize and then delete as objects, which is what EF can do as well.
In case of Dapper though, it probably doesn't respect the EF Core transaction.
