-- Daedalus migration 2 (#290): drops the six bytea "RowVersion" columns that guarded nothing.
-- EF Core mapped them as concurrency tokens, but PostgreSQL never fills a bytea on its own, so they stayed NULL
-- and no write was ever checked against them. The aggregates that need a guard use the xmin system column.
-- Rows are untouched; only the columns go.

ALTER TABLE "AgentSessions" DROP COLUMN "RowVersion";

ALTER TABLE "BrainstormSessions" DROP COLUMN "RowVersion";

ALTER TABLE "RepositoryConfigurations" DROP COLUMN "RowVersion";

ALTER TABLE "Projects" DROP COLUMN "RowVersion";

ALTER TABLE "CodeAnalysisRequests" DROP COLUMN "RowVersion";

ALTER TABLE "ExecutionSessions" DROP COLUMN "RowVersion";
