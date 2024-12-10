﻿// <auto-generated />
using System;
using System.Collections.Generic;
using AiCoreApi.Common.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiCoreApi.Migrations
{
    [DbContext(typeof(Db))]
    [Migration("20240626102624_RetriableTasks")]
    partial class RetriableTasks
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.5")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("AiCoreApi.Models.DbModels.ClientSsoModel", b =>
                {
                    b.Property<int>("ClientSsoId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("client_sso_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("ClientSsoId"));

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<string>("CreatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("created_by");

                    b.Property<int>("LoginType")
                        .HasColumnType("integer")
                        .HasColumnName("login_type");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("name");

                    b.Property<Dictionary<string, string>>("Settings")
                        .IsRequired()
                        .HasColumnType("jsonb")
                        .HasColumnName("settings");

                    b.HasKey("ClientSsoId")
                        .HasName("pk_client_sso");

                    b.ToTable("client_sso");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.ConnectionModel", b =>
                {
                    b.Property<int>("ConnectionId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("connection_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("ConnectionId"));

                    b.Property<Dictionary<string, string>>("Content")
                        .IsRequired()
                        .HasColumnType("jsonb")
                        .HasColumnName("content");

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<string>("CreatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("created_by");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("name");

                    b.Property<int>("Type")
                        .HasColumnType("integer")
                        .HasColumnName("type");

                    b.HasKey("ConnectionId")
                        .HasName("pk_connection");

                    b.ToTable("connection");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.DocumentMetadataModel", b =>
                {
                    b.Property<string>("DocumentId")
                        .HasColumnType("text")
                        .HasColumnName("document_id");

                    b.Property<DateTime>("CreatedTime")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_time");

                    b.Property<bool>("ImportFinished")
                        .HasColumnType("boolean")
                        .HasColumnName("import_finished");

                    b.Property<int>("IngestionId")
                        .HasColumnType("integer")
                        .HasColumnName("ingestion_id");

                    b.Property<DateTime>("LastMetadataUpdateTime")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("last_metadata_update_time");

                    b.Property<DateTime>("LastModifiedTime")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("last_modified_time");

                    b.Property<string>("Name")
                        .HasColumnType("text")
                        .HasColumnName("name");

                    b.Property<string>("Url")
                        .HasColumnType("text")
                        .HasColumnName("url");

                    b.HasKey("DocumentId")
                        .HasName("pk_document_metadata");

                    b.ToTable("document_metadata");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.GroupModel", b =>
                {
                    b.Property<int>("GroupId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("group_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("GroupId"));

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<string>("CreatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("created_by");

                    b.Property<string>("Description")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("description");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("name");

                    b.HasKey("GroupId")
                        .HasName("pk_groups");

                    b.ToTable("groups");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.IngestionModel", b =>
                {
                    b.Property<int>("IngestionId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("ingestion_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("IngestionId"));

                    b.Property<Dictionary<string, string>>("Content")
                        .IsRequired()
                        .HasColumnType("jsonb")
                        .HasColumnName("content");

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<string>("CreatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("created_by");

                    b.Property<DateTime>("LastSync")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("last_sync");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("name");

                    b.Property<string>("Note")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("note");

                    b.Property<int>("Type")
                        .HasColumnType("integer")
                        .HasColumnName("type");

                    b.Property<DateTime>("Updated")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updated");

                    b.HasKey("IngestionId")
                        .HasName("pk_ingestion");

                    b.ToTable("ingestion");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.LoginHistoryModel", b =>
                {
                    b.Property<int>("LoginHistoryId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("login_history_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("LoginHistoryId"));

                    b.Property<string>("Code")
                        .HasColumnType("text")
                        .HasColumnName("code");

                    b.Property<string>("CodeChallenge")
                        .HasColumnType("text")
                        .HasColumnName("code_challenge");

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<bool>("IsOffline")
                        .HasColumnType("boolean")
                        .HasColumnName("is_offline");

                    b.Property<string>("Login")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("login");

                    b.Property<int>("LoginId")
                        .HasColumnType("integer")
                        .HasColumnName("login_id");

                    b.Property<string>("RefreshToken")
                        .HasColumnType("text")
                        .HasColumnName("refresh_token");

                    b.Property<DateTime>("ValidUntilTime")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("valid_until_time");

                    b.HasKey("LoginHistoryId")
                        .HasName("pk_login_history");

                    b.ToTable("login_history");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.LoginModel", b =>
                {
                    b.Property<int>("LoginId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("login_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("LoginId"));

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<string>("CreatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("created_by");

                    b.Property<string>("Email")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("email");

                    b.Property<string>("FullName")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("full_name");

                    b.Property<bool>("IsEnabled")
                        .HasColumnType("boolean")
                        .HasColumnName("is_enabled");

                    b.Property<string>("Login")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("login");

                    b.Property<int>("LoginType")
                        .HasColumnType("integer")
                        .HasColumnName("login_type");

                    b.Property<string>("PasswordHash")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("password_hash");

                    b.Property<int>("Role")
                        .HasColumnType("integer")
                        .HasColumnName("role");

                    b.HasKey("LoginId")
                        .HasName("pk_login");

                    b.ToTable("login");

                    b.HasData(
                        new
                        {
                            LoginId = 1,
                            Created = new DateTime(2024, 6, 26, 10, 26, 23, 736, DateTimeKind.Utc).AddTicks(1808),
                            CreatedBy = "system",
                            Email = "admin@viacode.com",
                            FullName = "Admin",
                            IsEnabled = true,
                            Login = "admin@viacode.com",
                            LoginType = 1,
                            PasswordHash = "wh+Wm18D0z1D4E+PE252gg==",
                            Role = 2
                        });
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.RbacGroupSyncModel", b =>
                {
                    b.Property<int>("RbacGroupSyncId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("rbac_group_sync_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("RbacGroupSyncId"));

                    b.Property<string>("AiCoreGroupName")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("ai_core_group_name");

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<string>("CreatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("created_by");

                    b.Property<string>("RbacGroupName")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("rbac_group_name");

                    b.Property<string>("UpdatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("updated_by");

                    b.HasKey("RbacGroupSyncId")
                        .HasName("pk_rbac_group_sync");

                    b.ToTable("rbac_group_sync");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.RbacRoleSyncModel", b =>
                {
                    b.Property<int>("RbacRoleSyncId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("rbac_role_sync_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("RbacRoleSyncId"));

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<string>("CreatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("created_by");

                    b.Property<string>("RbacRoleName")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("rbac_role_name");

                    b.Property<string>("UpdatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("updated_by");

                    b.HasKey("RbacRoleSyncId")
                        .HasName("pk_rbac_role_sync");

                    b.ToTable("rbac_role_sync");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.SettingsModel", b =>
                {
                    b.Property<int>("SettingsId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("settings_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("SettingsId"));

                    b.Property<Dictionary<string, string>>("Content")
                        .IsRequired()
                        .HasColumnType("jsonb")
                        .HasColumnName("content");

                    b.Property<int?>("EntityId")
                        .HasColumnType("integer")
                        .HasColumnName("entity_id");

                    b.Property<int>("SettingsType")
                        .HasColumnType("integer")
                        .HasColumnName("settings_type");

                    b.HasKey("SettingsId")
                        .HasName("pk_settings");

                    b.ToTable("settings");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.SpentModel", b =>
                {
                    b.Property<int>("SpentId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("spent_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("SpentId"));

                    b.Property<DateTime>("Date")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("date");

                    b.Property<int>("LoginId")
                        .HasColumnType("integer")
                        .HasColumnName("login_id");

                    b.Property<int>("ModelType")
                        .HasColumnType("integer")
                        .HasColumnName("model_type");

                    b.Property<int>("TokensIncoming")
                        .HasColumnType("integer")
                        .HasColumnName("tokens_incoming");

                    b.Property<int>("TokensOutgoing")
                        .HasColumnType("integer")
                        .HasColumnName("tokens_outgoing");

                    b.HasKey("SpentId")
                        .HasName("pk_spent");

                    b.ToTable("spent");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.TagModel", b =>
                {
                    b.Property<int>("TagId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("tag_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("TagId"));

                    b.Property<string>("Color")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("color");

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<string>("CreatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("created_by");

                    b.Property<string>("Description")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("description");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("name");

                    b.HasKey("TagId")
                        .HasName("pk_tags");

                    b.ToTable("tags");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.TaskModel", b =>
                {
                    b.Property<int>("TaskId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("task_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("TaskId"));

                    b.Property<Dictionary<string, object>>("Context")
                        .IsRequired()
                        .HasColumnType("jsonb")
                        .HasColumnName("context");

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<string>("CreatedBy")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("created_by");

                    b.Property<string>("ErrorMessage")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("error_message");

                    b.Property<int>("IngestionId")
                        .HasColumnType("integer")
                        .HasColumnName("ingestion_id");

                    b.Property<bool>("IsRetriable")
                        .HasColumnType("boolean")
                        .HasColumnName("is_retriable");

                    b.Property<int>("State")
                        .HasColumnType("integer")
                        .HasColumnName("state");

                    b.Property<int>("Type")
                        .HasColumnType("integer")
                        .HasColumnName("type");

                    b.Property<DateTime>("Updated")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updated");

                    b.HasKey("TaskId")
                        .HasName("pk_task");

                    b.HasIndex("IngestionId")
                        .HasDatabaseName("ix_task_ingestion_id");

                    b.ToTable("task");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.TokenCostModel", b =>
                {
                    b.Property<int>("TokenCostId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("token_cost_id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("TokenCostId"));

                    b.Property<decimal>("Incoming")
                        .HasColumnType("numeric")
                        .HasColumnName("incoming");

                    b.Property<int>("ModelType")
                        .HasColumnType("integer")
                        .HasColumnName("model_type");

                    b.Property<decimal>("Outgoing")
                        .HasColumnType("numeric")
                        .HasColumnName("outgoing");

                    b.HasKey("TokenCostId")
                        .HasName("pk_token_cost");

                    b.ToTable("token_cost");

                    b.HasData(
                        new
                        {
                            TokenCostId = 1,
                            Incoming = 0m,
                            ModelType = 1,
                            Outgoing = 0m
                        },
                        new
                        {
                            TokenCostId = 2,
                            Incoming = 0.0000005m,
                            ModelType = 2,
                            Outgoing = 0.0000015m
                        },
                        new
                        {
                            TokenCostId = 3,
                            Incoming = 0.00001m,
                            ModelType = 3,
                            Outgoing = 0.00003m
                        });
                });

            modelBuilder.Entity("client_sso_x_groups", b =>
                {
                    b.Property<int>("ClientSsoId")
                        .HasColumnType("integer")
                        .HasColumnName("client_sso_id");

                    b.Property<int>("GroupsGroupId")
                        .HasColumnType("integer")
                        .HasColumnName("groups_group_id");

                    b.HasKey("ClientSsoId", "GroupsGroupId")
                        .HasName("pk_client_sso_x_groups");

                    b.HasIndex("GroupsGroupId")
                        .HasDatabaseName("ix_client_sso_x_groups_groups_group_id");

                    b.ToTable("client_sso_x_groups");
                });

            modelBuilder.Entity("logins_x_groups", b =>
                {
                    b.Property<int>("GroupsGroupId")
                        .HasColumnType("integer")
                        .HasColumnName("groups_group_id");

                    b.Property<int>("LoginsLoginId")
                        .HasColumnType("integer")
                        .HasColumnName("logins_login_id");

                    b.HasKey("GroupsGroupId", "LoginsLoginId")
                        .HasName("pk_logins_x_groups");

                    b.HasIndex("LoginsLoginId")
                        .HasDatabaseName("ix_logins_x_groups_logins_login_id");

                    b.ToTable("logins_x_groups");
                });

            modelBuilder.Entity("tags_x_groups", b =>
                {
                    b.Property<int>("GroupsGroupId")
                        .HasColumnType("integer")
                        .HasColumnName("groups_group_id");

                    b.Property<int>("TagsTagId")
                        .HasColumnType("integer")
                        .HasColumnName("tags_tag_id");

                    b.HasKey("GroupsGroupId", "TagsTagId")
                        .HasName("pk_tags_x_groups");

                    b.HasIndex("TagsTagId")
                        .HasDatabaseName("ix_tags_x_groups_tags_tag_id");

                    b.ToTable("tags_x_groups");
                });

            modelBuilder.Entity("tags_x_ingestions", b =>
                {
                    b.Property<int>("IngestionsIngestionId")
                        .HasColumnType("integer")
                        .HasColumnName("ingestions_ingestion_id");

                    b.Property<int>("TagsTagId")
                        .HasColumnType("integer")
                        .HasColumnName("tags_tag_id");

                    b.HasKey("IngestionsIngestionId", "TagsTagId")
                        .HasName("pk_tags_x_ingestions");

                    b.HasIndex("TagsTagId")
                        .HasDatabaseName("ix_tags_x_ingestions_tags_tag_id");

                    b.ToTable("tags_x_ingestions");
                });

            modelBuilder.Entity("tags_x_logins", b =>
                {
                    b.Property<int>("LoginsLoginId")
                        .HasColumnType("integer")
                        .HasColumnName("logins_login_id");

                    b.Property<int>("TagsTagId")
                        .HasColumnType("integer")
                        .HasColumnName("tags_tag_id");

                    b.HasKey("LoginsLoginId", "TagsTagId")
                        .HasName("pk_tags_x_logins");

                    b.HasIndex("TagsTagId")
                        .HasDatabaseName("ix_tags_x_logins_tags_tag_id");

                    b.ToTable("tags_x_logins");
                });

            modelBuilder.Entity("tags_x_rbac_role_sync", b =>
                {
                    b.Property<int>("RbacRoleSyncsRbacRoleSyncId")
                        .HasColumnType("integer")
                        .HasColumnName("rbac_role_syncs_rbac_role_sync_id");

                    b.Property<int>("TagsTagId")
                        .HasColumnType("integer")
                        .HasColumnName("tags_tag_id");

                    b.HasKey("RbacRoleSyncsRbacRoleSyncId", "TagsTagId")
                        .HasName("pk_tags_x_rbac_role_sync");

                    b.HasIndex("TagsTagId")
                        .HasDatabaseName("ix_tags_x_rbac_role_sync_tags_tag_id");

                    b.ToTable("tags_x_rbac_role_sync");
                });

            modelBuilder.Entity("AiCoreApi.Models.DbModels.TaskModel", b =>
                {
                    b.HasOne("AiCoreApi.Models.DbModels.IngestionModel", "Ingestion")
                        .WithMany()
                        .HasForeignKey("IngestionId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_task_ingestion_ingestion_id");

                    b.Navigation("Ingestion");
                });

            modelBuilder.Entity("client_sso_x_groups", b =>
                {
                    b.HasOne("AiCoreApi.Models.DbModels.ClientSsoModel", null)
                        .WithMany()
                        .HasForeignKey("ClientSsoId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_client_sso_x_groups_client_sso_client_sso_id");

                    b.HasOne("AiCoreApi.Models.DbModels.GroupModel", null)
                        .WithMany()
                        .HasForeignKey("GroupsGroupId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_client_sso_x_groups_groups_groups_group_id");
                });

            modelBuilder.Entity("logins_x_groups", b =>
                {
                    b.HasOne("AiCoreApi.Models.DbModels.GroupModel", null)
                        .WithMany()
                        .HasForeignKey("GroupsGroupId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_logins_x_groups_groups_groups_group_id");

                    b.HasOne("AiCoreApi.Models.DbModels.LoginModel", null)
                        .WithMany()
                        .HasForeignKey("LoginsLoginId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_logins_x_groups_login_logins_login_id");
                });

            modelBuilder.Entity("tags_x_groups", b =>
                {
                    b.HasOne("AiCoreApi.Models.DbModels.GroupModel", null)
                        .WithMany()
                        .HasForeignKey("GroupsGroupId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_tags_x_groups_groups_groups_group_id");

                    b.HasOne("AiCoreApi.Models.DbModels.TagModel", null)
                        .WithMany()
                        .HasForeignKey("TagsTagId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_tags_x_groups_tags_tags_tag_id");
                });

            modelBuilder.Entity("tags_x_ingestions", b =>
                {
                    b.HasOne("AiCoreApi.Models.DbModels.IngestionModel", null)
                        .WithMany()
                        .HasForeignKey("IngestionsIngestionId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_tags_x_ingestions_ingestion_ingestions_ingestion_id");

                    b.HasOne("AiCoreApi.Models.DbModels.TagModel", null)
                        .WithMany()
                        .HasForeignKey("TagsTagId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_tags_x_ingestions_tags_tags_tag_id");
                });

            modelBuilder.Entity("tags_x_logins", b =>
                {
                    b.HasOne("AiCoreApi.Models.DbModels.LoginModel", null)
                        .WithMany()
                        .HasForeignKey("LoginsLoginId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_tags_x_logins_login_logins_login_id");

                    b.HasOne("AiCoreApi.Models.DbModels.TagModel", null)
                        .WithMany()
                        .HasForeignKey("TagsTagId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_tags_x_logins_tags_tags_tag_id");
                });

            modelBuilder.Entity("tags_x_rbac_role_sync", b =>
                {
                    b.HasOne("AiCoreApi.Models.DbModels.RbacRoleSyncModel", null)
                        .WithMany()
                        .HasForeignKey("RbacRoleSyncsRbacRoleSyncId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_tags_x_rbac_role_sync_rbac_role_sync_rbac_role_syncs_rbac_r~");

                    b.HasOne("AiCoreApi.Models.DbModels.TagModel", null)
                        .WithMany()
                        .HasForeignKey("TagsTagId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_tags_x_rbac_role_sync_tags_tags_tag_id");
                });
#pragma warning restore 612, 618
        }
    }
}
