CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
CREATE TABLE "CodeAnalysisRequests" (
    "Id" uuid NOT NULL,
    "Title" character varying(255) NOT NULL,
    "Description" text NOT NULL,
    "RepositoryUrl" character varying(1024) NOT NULL,
    "RepositoryBranch" text,
    "CommitSha" text,
    "Platform" integer NOT NULL,
    "FilePath" character varying(1024) NOT NULL,
    "StartLine" integer,
    "EndLine" integer,
    "Type" integer NOT NULL,
    "Priority" integer NOT NULL,
    "Requirements" jsonb NOT NULL,
    "ReferencedDocuments" jsonb,
    "ExternalReferences" jsonb,
    "LocalWorkTreePath" text,
    "FeatureBranchName" text,
    "Status" integer NOT NULL,
    "CurrentIteration" integer NOT NULL,
    "MaxIterations" integer NOT NULL,
    "CompletionPromise" text,
    "LastPromptSent" text,
    "LastAiResponse" text,
    "PullRequestUrl" text,
    "CommitShaFinal" text,
    "ValidationResult" jsonb,
    "HasFailedValidation" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "StartedAt" timestamp with time zone,
    "CompletedAt" timestamp with time zone,
    "FailureReason" text,
    "CreatedBy" uuid,
    "AssignedTo" uuid,
    CONSTRAINT "PK_CodeAnalysisRequests" PRIMARY KEY ("Id")
);

CREATE TABLE "ExecutionSessions" (
    "Id" uuid NOT NULL,
    "WorkerName" character varying(256) NOT NULL,
    "StartedAt" timestamp with time zone NOT NULL,
    "LastHeartbeat" timestamp with time zone NOT NULL,
    "IsActive" boolean NOT NULL,
    "TasksCompleted" integer NOT NULL,
    CONSTRAINT "PK_ExecutionSessions" PRIMARY KEY ("Id")
);

CREATE TABLE "Projects" (
    "Id" uuid NOT NULL,
    "Version" text NOT NULL,
    "ProjectName" text NOT NULL,
    "Description" text NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ModifiedAt" timestamp with time zone,
    CONSTRAINT "PK_Projects" PRIMARY KEY ("Id")
);

CREATE TABLE "AnalysisIterations" (
    "Id" uuid NOT NULL,
    "CodeAnalysisRequestId" uuid NOT NULL,
    "IterationNumber" integer NOT NULL,
    "BranchName" text,
    "CommitSha" text,
    "PromptSent" text NOT NULL,
    "AiResponse" text NOT NULL,
    "CompilationSucceeded" boolean,
    "TestsPassed" boolean,
    "ValidationErrors" jsonb,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AnalysisIterations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AnalysisIterations_CodeAnalysisRequests_CodeAnalysisRequest~" FOREIGN KEY ("CodeAnalysisRequestId") REFERENCES "CodeAnalysisRequests" ("Id") ON DELETE CASCADE
);

CREATE TABLE "Tasks" (
    "Id" uuid NOT NULL,
    "TaskId" character varying(50) NOT NULL,
    "ProjectId" uuid NOT NULL,
    "Title" character varying(500) NOT NULL,
    "Description" character varying(2000) NOT NULL,
    "Priority" integer NOT NULL,
    "Status" integer NOT NULL,
    "Phase" character varying(100) NOT NULL,
    "ParallelGroup" integer NOT NULL,
    "EstimatedComplexity" integer NOT NULL,
    "Prompt" character varying(8000) NOT NULL,
    "CompletionPromise" character varying(1000) NOT NULL,
    "MaxIterations" integer NOT NULL,
    "CurrentSessionId" uuid,
    "Result" character varying(50000),
    "IterationCount" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CompletedAt" timestamp with time zone,
    "Learnings" character varying(10000) NOT NULL,
    "LearningsUpdatedAt" timestamp with time zone,
    "Dependencies" text[] NOT NULL,
    "FilesToModify" text[] NOT NULL,
    CONSTRAINT "PK_Tasks" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Tasks_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
);

CREATE TABLE "TaskExecutions" (
    "Id" uuid NOT NULL,
    "TaskId" uuid NOT NULL,
    "SessionId" uuid NOT NULL,
    "IterationNumber" integer NOT NULL,
    "Prompt" character varying(8000) NOT NULL,
    "LlmResponse" character varying(50000) NOT NULL,
    "CompletionPromiseFound" boolean NOT NULL,
    "ExecutedAt" timestamp with time zone NOT NULL,
    "ExecutionDuration" interval NOT NULL,
    "Error" character varying(2000),
    CONSTRAINT "PK_TaskExecutions" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_TaskExecutions_Tasks_TaskId" FOREIGN KEY ("TaskId") REFERENCES "Tasks" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_AnalysisIteration_CreatedAt" ON "AnalysisIterations" ("CreatedAt");

CREATE INDEX "IX_AnalysisIteration_RequestId" ON "AnalysisIterations" ("CodeAnalysisRequestId");

CREATE INDEX "IX_CodeAnalysis_CreatedAt" ON "CodeAnalysisRequests" ("CreatedAt");

CREATE INDEX "IX_CodeAnalysis_Priority" ON "CodeAnalysisRequests" ("Priority");

CREATE INDEX "IX_CodeAnalysis_RepositoryUrl" ON "CodeAnalysisRequests" ("RepositoryUrl");

CREATE INDEX "IX_CodeAnalysis_Status" ON "CodeAnalysisRequests" ("Status");

CREATE INDEX "IX_Session_IsActive" ON "ExecutionSessions" ("IsActive");

CREATE INDEX "IX_Session_LastHeartbeat" ON "ExecutionSessions" ("LastHeartbeat");

CREATE INDEX "IX_TaskExecution_ExecutedAt" ON "TaskExecutions" ("ExecutedAt");

CREATE INDEX "IX_TaskExecution_SessionId" ON "TaskExecutions" ("SessionId");

CREATE INDEX "IX_TaskExecution_TaskId" ON "TaskExecutions" ("TaskId");

CREATE INDEX "IX_Task_CreatedAt" ON "Tasks" ("CreatedAt");

CREATE INDEX "IX_Task_SessionId" ON "Tasks" ("CurrentSessionId");

CREATE INDEX "IX_Task_Status" ON "Tasks" ("Status");

CREATE INDEX "IX_Tasks_ProjectId" ON "Tasks" ("ProjectId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260209134036_AddMissingTaskColumns', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Tasks" ADD "RowVersion" bytea;

ALTER TABLE "Projects" ALTER COLUMN "Version" TYPE character varying(50);

ALTER TABLE "Projects" ALTER COLUMN "ProjectName" TYPE character varying(256);

ALTER TABLE "Projects" ALTER COLUMN "Description" TYPE character varying(2000);

ALTER TABLE "Projects" ADD "RowVersion" bytea;

ALTER TABLE "ExecutionSessions" ADD "RowVersion" bytea;

CREATE INDEX "IX_Project_CreatedAt" ON "Projects" ("CreatedAt");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260210073742_AddRowVersionConcurrencyTokens', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "CodeAnalysisRequests" ADD "CancelledAt" timestamp with time zone;

ALTER TABLE "CodeAnalysisRequests" ADD "RowVersion" bytea;

CREATE TABLE "RepositoryConfigurations" (
    "Id" uuid NOT NULL,
    "Name" character varying(256) NOT NULL,
    "Url" character varying(1024) NOT NULL,
    "Platform" character varying(50) NOT NULL,
    "DefaultBranch" character varying(256) NOT NULL,
    "AuthenticationMethod" character varying(100),
    "CredentialIdentifier" character varying(512),
    "IsActive" boolean NOT NULL,
    "Description" character varying(2000),
    "CreatedAt" timestamp with time zone NOT NULL,
    "ModifiedAt" timestamp with time zone,
    "LastUsedAt" timestamp with time zone,
    "RowVersion" bytea,
    CONSTRAINT "PK_RepositoryConfigurations" PRIMARY KEY ("Id")
);

CREATE TABLE "StructuredLearnings" (
    "Id" uuid NOT NULL,
    "Category" integer NOT NULL,
    "Pattern" character varying(2000) NOT NULL,
    "Resolution" character varying(2000) NOT NULL,
    "SourceTaskId" uuid,
    "ProjectId" uuid,
    "Severity" integer NOT NULL,
    "HitCount" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastReferencedAt" timestamp with time zone,
    "Tags" text[] NOT NULL,
    CONSTRAINT "PK_StructuredLearnings" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_RepositoryConfiguration_CreatedAt" ON "RepositoryConfigurations" ("CreatedAt");

CREATE INDEX "IX_RepositoryConfiguration_IsActive" ON "RepositoryConfigurations" ("IsActive");

CREATE INDEX "IX_RepositoryConfiguration_Name" ON "RepositoryConfigurations" ("Name");

CREATE INDEX "IX_StructuredLearning_Category" ON "StructuredLearnings" ("Category");

CREATE INDEX "IX_StructuredLearning_CreatedAt" ON "StructuredLearnings" ("CreatedAt");

CREATE INDEX "IX_StructuredLearning_HitCount" ON "StructuredLearnings" ("HitCount");

CREATE INDEX "IX_StructuredLearning_ProjectId" ON "StructuredLearnings" ("ProjectId");

CREATE INDEX "IX_StructuredLearning_Severity" ON "StructuredLearnings" ("Severity");

CREATE INDEX "IX_StructuredLearning_SourceTaskId" ON "StructuredLearnings" ("SourceTaskId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260223170600_AddRepositoryConfigurations', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Projects" ADD "DefaultBranch" character varying(100) NOT NULL DEFAULT 'main';

ALTER TABLE "Projects" ADD "RepositoryUrl" character varying(2000) NOT NULL DEFAULT '';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260227124614_AddProjectRepositoryUrl', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "TaskExecutions" ADD "InputTokens" integer NOT NULL DEFAULT 0;

ALTER TABLE "TaskExecutions" ADD "ModelId" character varying(100);

ALTER TABLE "TaskExecutions" ADD "OutputTokens" integer NOT NULL DEFAULT 0;

ALTER TABLE "AnalysisIterations" ADD "InputTokens" integer NOT NULL DEFAULT 0;

ALTER TABLE "AnalysisIterations" ADD "ModelId" character varying(100);

ALTER TABLE "AnalysisIterations" ADD "OutputTokens" integer NOT NULL DEFAULT 0;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260301172140_AddTokenUsageColumns', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE EXTENSION IF NOT EXISTS vector;

ALTER TABLE "StructuredLearnings" ADD "Embedding" vector(384);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260302080051_AddSemanticEmbeddings', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE INDEX IF NOT EXISTS "IX_StructuredLearnings_Embedding"
ON "StructuredLearnings"
USING hnsw ("Embedding" vector_cosine_ops);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260302120000_AddEmbeddingHnswIndex', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "BrainstormSessions" (
    "Id" uuid NOT NULL,
    "ProjectId" uuid NOT NULL,
    "Phase" integer NOT NULL,
    "DesignDocument" text,
    "ImplementationPlan" text,
    "PhaseCompleteSignaled" boolean NOT NULL DEFAULT FALSE,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CompletedAt" timestamp with time zone,
    "RowVersion" bytea,
    CONSTRAINT "PK_BrainstormSessions" PRIMARY KEY ("Id")
);

CREATE TABLE "BrainstormMessages" (
    "Id" uuid NOT NULL,
    "BrainstormSessionId" uuid NOT NULL,
    "Role" integer NOT NULL,
    "Content" text NOT NULL,
    "Phase" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_BrainstormMessages" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BrainstormMessages_BrainstormSessions_BrainstormSessionId" FOREIGN KEY ("BrainstormSessionId") REFERENCES "BrainstormSessions" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_BrainstormMessage_SessionId" ON "BrainstormMessages" ("BrainstormSessionId");

CREATE INDEX "IX_BrainstormSession_Phase" ON "BrainstormSessions" ("Phase");

CREATE INDEX "IX_BrainstormSession_ProjectId" ON "BrainstormSessions" ("ProjectId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260302134329_AddBrainstormSessions', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "AgentSessions" (
    "Id" uuid NOT NULL,
    "AgentId" uuid NOT NULL,
    "OwnerId" character varying(256) NOT NULL,
    "State" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastActivityAt" timestamp with time zone NOT NULL,
    "TurnCount" integer NOT NULL,
    "TotalInputTokens" bigint NOT NULL,
    "TotalOutputTokens" bigint NOT NULL,
    "RowVersion" bytea,
    CONSTRAINT "PK_AgentSessions" PRIMARY KEY ("Id")
);

CREATE TABLE "AgentMessages" (
    "Id" uuid NOT NULL,
    "SessionId" uuid NOT NULL,
    "Sequence" integer NOT NULL,
    "Role" character varying(32) NOT NULL,
    "ContentJson" jsonb NOT NULL,
    "InputTokens" integer,
    "OutputTokens" integer,
    "ModelId" character varying(128),
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AgentMessages" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AgentMessages_AgentSessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "AgentSessions" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_AgentMessage_Session_Sequence" ON "AgentMessages" ("SessionId", "Sequence");

CREATE INDEX "IX_AgentSession_AgentId" ON "AgentSessions" ("AgentId");

CREATE INDEX "IX_AgentSession_Owner_CreatedAt" ON "AgentSessions" ("OwnerId", "CreatedAt");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260816174546_AddAgentSessions', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "AgentMemories" (
    "Id" uuid NOT NULL,
    "OwnerId" character varying(256) NOT NULL,
    "AgentId" uuid,
    "Kind" character varying(32) NOT NULL,
    "Text" text NOT NULL,
    "Source" character varying(256) NOT NULL,
    "Importance" double precision NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "LastRecalledAt" timestamp with time zone,
    "RecallCount" integer NOT NULL,
    "IsArchived" boolean NOT NULL,
    "IndexPending" boolean NOT NULL,
    "Tags" text[] NOT NULL,
    CONSTRAINT "PK_AgentMemories" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_AgentMemory_CreatedAt_Id" ON "AgentMemories" ("CreatedAt", "Id");

CREATE INDEX "IX_AgentMemory_IndexPending" ON "AgentMemories" ("IndexPending");

CREATE INDEX "IX_AgentMemory_IsArchived" ON "AgentMemories" ("IsArchived");

CREATE INDEX "IX_AgentMemory_Owner_Agent" ON "AgentMemories" ("OwnerId", "AgentId");

CREATE INDEX "IX_AgentMemory_Owner_Kind" ON "AgentMemories" ("OwnerId", "Kind");

INSERT INTO "AgentMemories"
    ("Id","OwnerId","AgentId","Kind","Text","Tags","Source","Importance","CreatedAt","UpdatedAt","LastRecalledAt","RecallCount","IsArchived","IndexPending")
SELECT
    s."Id",
    'daedalus',
    NULL,
    'learning',
    left(CASE WHEN s."Pattern" = s."Resolution" THEN s."Pattern" ELSE s."Pattern" || E'\n' || s."Resolution" END, 4000),
    ARRAY[
        CASE s."Category" WHEN 0 THEN 'errorpattern' WHEN 1 THEN 'successpattern' WHEN 2 THEN 'codeconvention' WHEN 3 THEN 'dependencyinfo' WHEN 4 THEN 'architecturedecision' ELSE 'general' END,
        CASE s."Severity" WHEN 0 THEN 'low' WHEN 1 THEN 'medium' WHEN 2 THEN 'high' WHEN 3 THEN 'critical' ELSE 'medium' END
    ] || COALESCE((
        SELECT array_agg(d.tag ORDER BY d.ord)
        FROM (
            SELECT n.tag, min(n.ord) AS ord
            FROM (
                SELECT left(lower(btrim(u.t)), 32) AS tag, u.ord
                FROM unnest(s."Tags") WITH ORDINALITY AS u(t, ord)
            ) n
            WHERE n.tag IS NOT NULL AND n.tag <> ''
            GROUP BY n.tag
            ORDER BY min(n.ord)
            LIMIT 8
        ) d
    ), '{}'::text[]),
    CASE WHEN s."SourceTaskId" IS NULL THEN 'migration' ELSE 'ralph:task/' || s."SourceTaskId"::text END,
    CASE s."Severity" WHEN 3 THEN 1.0 WHEN 2 THEN 0.8 WHEN 1 THEN 0.5 ELSE 0.3 END,
    s."CreatedAt",
    s."CreatedAt",
    s."LastReferencedAt",
    s."HitCount",
    false,
    true
FROM "StructuredLearnings" s;

DROP INDEX IF EXISTS "IX_StructuredLearnings_Embedding";

DROP TABLE "StructuredLearnings";

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260817194349_AddAgentMemories', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "Skills" (
    "Name" character varying(64) NOT NULL,
    "Description" character varying(300) NOT NULL,
    "Body" text NOT NULL,
    "SourcePath" character varying(1024) NOT NULL,
    "ContentHash" character varying(128) NOT NULL,
    "IsActive" boolean NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "Tags" text[] NOT NULL,
    CONSTRAINT "PK_Skills" PRIMARY KEY ("Name")
);

CREATE INDEX "IX_Skill_IsActive" ON "Skills" ("IsActive");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260819152830_AddSkills', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "ChannelConversations" (
    "Id" uuid NOT NULL,
    "ChannelId" character varying(32) NOT NULL,
    "ConversationId" character varying(128) NOT NULL,
    "SessionId" uuid NOT NULL,
    "AgentId" uuid NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastActivityAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ChannelConversations" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX "IX_ChannelConversation_Channel_Conversation" ON "ChannelConversations" ("ChannelId", "ConversationId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260821155023_AddChannelConversations', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "OutboxMessages" (
    "Id" uuid NOT NULL,
    "TypeName" character varying(256) NOT NULL,
    "Payload" bytea NOT NULL,
    "Status" integer NOT NULL,
    "RetryCount" integer NOT NULL,
    "NextRetryAt" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ProcessedAt" timestamp with time zone,
    "DeadLetterError" text,
    CONSTRAINT "PK_OutboxMessages" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_OutboxMessages_Status_NextRetryAt" ON "OutboxMessages" ("Status", "NextRetryAt");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260821163952_AddChannelMessageOutbox', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "ScheduledRuns" (
    "Id" uuid NOT NULL,
    "Name" character varying(64) NOT NULL,
    "Cron" character varying(128) NOT NULL,
    "Trigger" character varying(128) NOT NULL,
    "ChannelId" character varying(32) NOT NULL,
    "ConversationId" character varying(128) NOT NULL,
    "PrincipalId" character varying(128) NOT NULL,
    "Roles" character varying(512) NOT NULL,
    "Origin" character varying(16) NOT NULL,
    "NextRunAt" timestamp with time zone NOT NULL,
    "LastRunAt" timestamp with time zone,
    "Enabled" boolean NOT NULL,
    "MissedOccurrences" integer NOT NULL,
    CONSTRAINT "PK_ScheduledRuns" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_ScheduledRun_Enabled_NextRunAt" ON "ScheduledRuns" ("Enabled", "NextRunAt");

CREATE UNIQUE INDEX "IX_ScheduledRun_Name" ON "ScheduledRuns" ("Name");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260917190132_AddScheduledRuns', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "ScheduledRunExecutions" (
    "Id" uuid NOT NULL,
    "ScheduleId" uuid NOT NULL,
    "OccurrenceAt" timestamp with time zone NOT NULL,
    "Step" character varying(16) NOT NULL,
    "Findings" text,
    "Digest" text,
    "ChannelId" character varying(32) NOT NULL,
    "ConversationId" character varying(128) NOT NULL,
    "PrincipalId" character varying(128) NOT NULL,
    "Roles" character varying(512) NOT NULL,
    "Attempts" integer NOT NULL,
    "LastError" text,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ScheduledRunExecutions" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX "IX_ScheduledRunExecution_Schedule_Occurrence" ON "ScheduledRunExecutions" ("ScheduleId", "OccurrenceAt");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260917223546_AddScheduledRunExecutions', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "ScheduledRunExecutions" ADD "FailedAtStep" character varying(16);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260918140148_AddFailedAtStep', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "ScheduledRuns" ADD "Repository" character varying(256);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260919085910_AddScheduledRunRepository', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "SkillVersions" (
    "Name" character varying(64) NOT NULL,
    "ContentHash" character varying(128) NOT NULL,
    "Description" character varying(300) NOT NULL,
    "Body" text NOT NULL,
    "SourcePath" character varying(1024) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "Tags" text[] NOT NULL,
    CONSTRAINT "PK_SkillVersions" PRIMARY KEY ("Name", "ContentHash")
);

INSERT INTO "SkillVersions" ("Name", "ContentHash", "Description", "Body", "Tags", "SourcePath", "CreatedAt")
SELECT "Name", "ContentHash", "Description", "Body", "Tags", "SourcePath", "UpdatedAt" FROM "Skills"
ON CONFLICT DO NOTHING;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260924084252_AddSkillVersions', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "RoleCharters" (
    "Role" character varying(64) NOT NULL,
    "CurrentHash" character varying(128) NOT NULL,
    "IsActive" boolean NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_RoleCharters" PRIMARY KEY ("Role")
);

CREATE TABLE "RoleCharterVersions" (
    "Role" character varying(64) NOT NULL,
    "ContentHash" character varying(128) NOT NULL,
    "Description" character varying(300) NOT NULL,
    "Instructions" text NOT NULL,
    "Model" character varying(128),
    "SourcePath" character varying(1024) NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "Skills" text[] NOT NULL,
    CONSTRAINT "PK_RoleCharterVersions" PRIMARY KEY ("Role", "ContentHash")
);

CREATE INDEX "IX_RoleCharterHead_IsActive" ON "RoleCharters" ("IsActive");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260924092645_AddRoleCharters', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "OutboxMessages" ADD "LockedBy" character varying(128);

ALTER TABLE "OutboxMessages" ADD "LockedUntil" timestamp with time zone;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260927122827_AddOutboxLease', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "WorkflowRunRecords" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "RunId" uuid NOT NULL,
    "Seq" bigint NOT NULL,
    "Node" character varying(128) NOT NULL,
    "Kind" character varying(32) NOT NULL,
    "PrincipalId" character varying(256) NOT NULL,
    "StartedById" character varying(256),
    "PayloadJson" jsonb NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_WorkflowRunRecords" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_WorkflowRunRecords_RunId_Kind_Seq" ON "WorkflowRunRecords" ("RunId", "Kind", "Seq");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260927233907_AddWorkflowRunRecords', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Tasks" ADD "WorkflowRunId" uuid;

ALTER TABLE "Tasks" ADD CONSTRAINT "CK_Tasks_Status_Stored" CHECK ("Status" BETWEEN 0 AND 4);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261010051822_AddTaskWorkflowRunId', '10.0.12');

COMMIT;

START TRANSACTION;
DELETE FROM "WorkflowRunRecords" d
USING "WorkflowRunRecords" k
WHERE d."Kind" = 'node-usage' AND k."Kind" = 'node-usage'
  AND d."RunId" = k."RunId" AND d."Seq" = k."Seq" AND d."Id" > k."Id";

CREATE UNIQUE INDEX "UX_WorkflowRunRecords_NodeUsage_RunId_Seq" ON "WorkflowRunRecords" ("RunId", "Seq") WHERE "Kind" = 'node-usage';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261010055033_AddNodeUsageUniqueIndex', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Tasks" DROP COLUMN "RowVersion";

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261010080350_MakeTaskRowVersionXmin', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE INDEX "IX_Tasks_WorkflowRunId" ON "Tasks" ("WorkflowRunId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261012210937_AddTaskWorkflowRunIdIndex', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE UNIQUE INDEX "UX_WorkflowRunRecords_NodeSelection_RunId_Seq" ON "WorkflowRunRecords" ("RunId", "Seq") WHERE "Kind" = 'node-selection';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261014090300_AddNodeSelectionUniqueIndex', '10.0.12');

COMMIT;

