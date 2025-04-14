using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiCoreApi.Common.Data
{
    public class Db : DbContext
    {
        public DbSet<TagModel> Tags { get; set; }
        public DbSet<GroupModel> Groups { get; set; }
        public DbSet<SettingsModel> Settings { get; set; }
        public DbSet<IngestionModel> Ingestions { get; set; }
        public DbSet<TaskModel> Tasks { get; set; }
        public DbSet<ConnectionModel?> Connections { get; set; }
        public DbSet<LoginModel> Login { get; set; }
        public DbSet<LoginHistoryModel> LoginHistory { get; set; }
        public DbSet<DocumentMetadataModel> DocumentMetadata { get; set; }
        public DbSet<ClientSsoModel> ClientSso { get; set; }
        public DbSet<RbacGroupSyncModel> RbacGroupSync { get; set; }
        public DbSet<RbacRoleSyncModel> RbacRoleSync { get; set; }
        public DbSet<SpentModel> Spent { get; set; }
        public DbSet<AgentModel> Agents { get; set; }
        public DbSet<SchedulerAgentTaskModel> SchedulerAgentTasks { get; set; }
        public DbSet<DebugLogModel> DebugLog { get; set; }
        public DbSet<WorkspaceModel> Workspaces { get; set; }


        private readonly IDbQuery _dbQuery;
        private readonly ILogger<Db> _logger;
        private readonly IDataSourceProvider _dataSourceProvider;

        public Db(IDbQuery dbQuery, ILogger<Db> logger, IDataSourceProvider dataSourceProvider)
        {
            _dbQuery = dbQuery;
            _logger = logger;
            _dataSourceProvider = dataSourceProvider;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            /* 
            After updating to EF Core version: 9.0.0, the error “The model for context ‘Db’ has pending changes.” occurs.
            To avoid this error we suppress the corresponding warning
            Reference: https://github.com/dotnet/efcore/issues/34431
            */
            optionsBuilder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

            if (optionsBuilder.IsConfigured) return;
            optionsBuilder.UseNpgsql(_dataSourceProvider.DataSource);
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<IngestionModel>()
                .HasMany(e => e.Tags)
                .WithMany(e => e.Ingestions)
                .UsingEntity("tags_x_ingestions");

            builder.Entity<GroupModel>()
                .HasMany(e => e.Tags)
                .WithMany(e => e.Groups)
                .UsingEntity("tags_x_groups");

            builder.Entity<GroupModel>()
                .HasMany(e => e.Logins)
                .WithMany(e => e.Groups)
                .UsingEntity("logins_x_groups");

            builder.Entity<LoginModel>()
                .HasMany(e => e.Tags)
                .WithMany(e => e.Logins)
                .UsingEntity("tags_x_logins");

            builder.Entity<LoginModel>()
                .HasMany(e => e.Groups)
                .WithMany(e => e.Logins)
                .UsingEntity("logins_x_groups");

            builder.Entity<ClientSsoModel>()
                .HasMany(e => e.Groups)
                .WithMany(e => e.ClientSso)
                .UsingEntity("client_sso_x_groups");

            builder.Entity<RbacRoleSyncModel>()
                .HasMany(e => e.Tags)
                .WithMany(e => e.RbacRoleSyncs)
                .UsingEntity("tags_x_rbac_role_sync");

            builder.Entity<AgentModel>()
                .HasMany(e => e.Tags)
                .WithMany(e => e.Agents)
                .UsingEntity("tags_x_agents");

            builder.Entity<WorkspaceModel>()
                .HasMany(e => e.Tags)
                .WithMany(e => e.Workspaces)
                .UsingEntity("tags_x_workspaces");

            foreach (var entity in builder.Model.GetEntityTypes())
            {
                entity.SetTableName(entity.GetTableName().ToSnakeCase());
                foreach (var property in entity.GetProperties())
                {
                    property.SetColumnName(property.Name.ToSnakeCase());
                }

                foreach (var key in entity.GetKeys())
                {
                    key.SetName(key.GetName().ToSnakeCase());
                }

                foreach (var key in entity.GetForeignKeys())
                {
                    key.SetConstraintName(key.GetConstraintName().ToSnakeCase());
                }

                foreach (var index in entity.GetIndexes())
                {
                    index.SetDatabaseName(index.GetDatabaseName().ToSnakeCase());
                }
            }

            builder.Entity<LoginModel>().HasData(
                new LoginModel
                {
                    LoginId = 1,
                    CreatedBy = "system",
                    Login = "admin@viacode.com",
                    Email = "admin@viacode.com",
                    IsEnabled = true,
                    PasswordHash = "default".GetHash(),
                    LoginType = LoginTypeEnum.Password,
                    Role = RoleEnum.Admin,
                    FullName = "Admin",
                    Tags = new List<TagModel>(),
                    Groups = new List<GroupModel>(),
                    Created = DateTime.UtcNow,
                    TokensLimit = 0,
                });
        }
    }
}