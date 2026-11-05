using ZeroAlloc.ORM;

// Phase 3.5: every query in this assembly renders PostgreSQL, and every DateTime column EF Core created is a
// timestamptz, Npgsql's EF default. Declaring it binds a DateTime parameter as the column's type, so a value of the
// wrong Kind fails loudly instead of shifting by the session's offset.
[assembly: Dialect(SqlDialect.PostgreSql)]
[assembly: EntityMapDefaults(DateTimeStorage = DateTimeStorage.TimestampTz)]
