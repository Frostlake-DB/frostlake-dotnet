# frostlake-dotnet

An ADO.NET provider for [Frostlake](https://frostlake.dev), speaking the engine's HTTP
protocol against a running `DatabaseHttpServer`. .NET 8, zero runtime dependencies —
transport is `HttpClient`, parsing is `System.Text.Json`, both in the BCL. Because it
implements the standard `System.Data.Common` surface, anything that rides on ADO.NET
(Dapper included) works on top.

## Engine version

Requires a Frostlake engine **0.2.0 or newer**. Ask a running server which one it is with
`SELECT CURRENT_VERSION()` — every release answers it, so the check works against any engine.

The driver versions independently of the engine: it speaks the HTTP protocol, not
the jar, so this is a floor rather than a lockstep pin.

## Usage

```csharp
using Frostlake.Data;

using var connection = new FrostlakeConnection("frostlake://localhost:18082/MY_DB?schema=PUBLIC");
// or ADO.NET style: "Host=localhost;Port=18082;Database=MY_DB;Schema=PUBLIC"
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "INSERT INTO people VALUES (?, ?)";
((FrostlakeCommand)command).Parameters.AddWithValue("", 1);
((FrostlakeCommand)command).Parameters.AddWithValue("", "Ada");
int inserted = command.ExecuteNonQuery(); // 1

command.Parameters.Clear();
command.CommandText = "SELECT id, name FROM people WHERE id = ?";
((FrostlakeCommand)command).Parameters.AddWithValue("", 1);
using var reader = command.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"{reader.GetInt64(0)} {reader.GetString(1)}");
}
```

Failed statements throw `FrostlakeException` carrying the engine's error message.
`BeginTransaction()` opens a `BEGIN … COMMIT/ROLLBACK` span (disposing an unfinished
transaction rolls back). Multi-statement responses are walked with `reader.NextResult()`, once the pack has been asked for —
at the default of one statement, a request carrying more is refused. A command can ask for itself,
with `MULTI_STATEMENT_COUNT` as a statement parameter, the way the account's own provider takes it:

```csharp
using var command = connection.CreateCommand();
command.CommandText = "SELECT 1; SELECT 2";
command.Parameters.AddWithValue("MULTI_STATEMENT_COUNT", 2);
using var reader = command.ExecuteReader();
```

That parameter is consumed by the command rather than bound into the SQL, and `0` allows any number.
It applies to that one command and does not change the session's `MULTI_STATEMENT_COUNT`, so nothing
else on the connection is affected. `ALTER SESSION SET MULTI_STATEMENT_COUNT = n` still works when a
whole session should carry packs.
Provider-agnostic code can go through `FrostlakeProviderFactory.Instance`
(`DbProviderFactories.RegisterFactory("Frostlake.Data", …)`).

### Dapper

```csharp
using Dapper;

var crew = connection.Query<CrewMember>("SELECT id, name FROM crew ORDER BY id");
var one = connection.Query<CrewMember>(
    "SELECT id, name FROM crew WHERE id = @id", new { id = 2 });
```

### Bind values

Parameters are inlined client-side. `?` placeholders are filled positionally in
collection order; `@name` and `:name` placeholders are filled by parameter name (any
`@`/`:` prefix on the parameter's own name is ignored, and a name may repeat). The two
styles can be mixed in one statement.

A `@name`/`:name` is substituted only when a parameter of that name was supplied **and**
the marker does not directly follow the end of an operand — a name, number or
`$`variable, a closing `)`, `]` or `}`, the closing quote of a literal or quoted
identifier, or a `?` — and `::` is always read as the cast operator. So stage references
(`COPY INTO t FROM @my_stage`), VARIANT paths (`v:address:city`, `{'a':1}:a`, `'…':a`,
`?:a`), casts (`v::date`) and scripting assignment (`LET x := 1`) pass through untouched
even when a parameter happens to share the name. A marker after a space still binds
(`WHERE v = :a`).

Placeholders inside string literals, quoted identifiers, `$$…$$` bodies and
`--`/`//`/`/* */` comments are left alone. A named parameter may go unused — Dapper hands
over every property of the parameter object — but a **positional** value that no `?`
consumed is an error rather than a silent drop, because that mismatch otherwise re-sends
a stale value on the next execute. Formatting is culture-invariant.

| .NET value | SQL literal |
| --- | --- |
| `null` / `DBNull` | `NULL` |
| `bool` | `TRUE` / `FALSE` |
| integer types, `decimal`, `float`, `double` | as written (invariant culture) |
| `string` / `char` / `Guid` | `'…'` (backslashes and quotes escaped) |
| `DateTime` | `'…'::TIMESTAMP_NTZ` |
| `DateTimeOffset` | `'…'::TIMESTAMP_TZ` |
| `DateOnly` / `TimeOnly` / `TimeSpan` | `'…'::DATE` / `'…'::TIME` |
| `byte[]` | `X'hex'` |
| `IEnumerable` | `[…]` (elements formatted recursively) |

### Result types

Integral `NUMBER` → `long`, scaled `NUMBER` → `decimal`, `FLOAT` → `double`, `BOOLEAN` →
`bool`, `DATE`/`TIMESTAMP_NTZ` → `DateTime`, `TIMESTAMP_TZ`/`TIMESTAMP_LTZ` →
`DateTimeOffset`, `TIME` → `TimeSpan`, `BINARY` → `byte[]`, everything else — including
`VARIANT`/`OBJECT`/`ARRAY` as their JSON text — `string`.

The zoned timestamps follow Snowflake's own .NET connector, so neither depends on the
client machine's time zone: a `TIMESTAMP_TZ` keeps the offset it was written with, and a
`TIMESTAMP_LTZ` arrives at the session time zone's offset. `GetDateTime` reads a
`TIMESTAMP_LTZ` as its wall-clock time in the session's zone (`DateTimeKind.Local`) and
refuses a `TIMESTAMP_TZ`, whose offset a `DateTime` cannot keep — read it with
`GetFieldValue<DateTimeOffset>` or `GetValue`. A value .NET cannot hold (an offset past
±14:00, an instant before year 1) turns its column into text, like any other unreadable
value.

A column's CLR type is settled once per result set from the values it actually carries,
so `GetFieldType(i)` always matches the type `GetValue(i)` returns for **every** row of
that column — what `DataTable.Load`, data adapters and Dapper rely on. This matters
because the engine reports a plain `INTEGER` as `NUMBER(38,0)`: the declared precision
cannot tell an ordinary counter from a 38-digit value. An integral column widens to
`decimal`, and then to the exact digit string, only when a value in it does not fit;
columns of ordinary integers stay `long`.

`GetSchemaTable()` describes the current result set, so `DataTable.Load` and
`DbDataAdapter.Fill` work.

## Running the tests

The integration tests boot a real server from the engine's compiled classes:

```sh
export JAVA_HOME=/path/to/jdk17
export FROSTLAKE_CLASSPATH="/path/to/frostlake/engine/target/classes:<engine deps>"
dotnet test
```

Without `FROSTLAKE_CLASSPATH` the engine-backed tests report as **skipped** — never as
passed — so a run with no server cannot be mistaken for a green suite. The unit tests
(substitution, connection strings, parameters and the reader, which is driven from
wire-shaped JSON) still run and cover most of the driver on their own.

With `FL_CORPUS` naming the engine's testkit directory, the run also replays the testkit corpus
through the provider (see below), on a server of its own from `FROSTLAKE_CLASSPATH`, or on the one
`FROSTLAKE_URL` names; without `FL_CORPUS` that test reports as skipped:

```sh
FL_CORPUS=/path/to/frostlake/engine/src/test/resources/testkit dotnet test
```

## Testkit corpus runner

`Frostlake.Data.Testkit/` replays the engine's testkit corpus — the language-neutral JSON
suites in the engine repository (`engine/src/test/resources/testkit/suites`, format in the
`SCHEMA.md` beside them) — through this provider, as the engine's reference runner does. It
is a console project, which `dotnet test` runs in-process when `FL_CORPUS` is set. To run it on
its own, start a server, a fresh one (the corpus creates databases, users and warehouses outside
its test database, so a second replay on the same engine collides with the first), and point the
runner at it from the checkout root:

```sh
java -cp "/path/to/frostlake/engine/target/classes:<engine deps>" \
    dev.frostlake.http.DatabaseHttpServer 18082 &
FL_CORPUS=/path/to/frostlake/engine/src/test/resources/testkit \
    FROSTLAKE_URL=frostlake://127.0.0.1:18082 dotnet run --project Frostlake.Data.Testkit --nologo
```

- `FL_CORPUS` — the testkit directory, whose `suites/*.json` are replayed; best given as an
  absolute path (a relative one resolves against the working directory). Unset, the runner says
  so and exits 0; set to a directory without suites, it fails;
- `FROSTLAKE_TESTKIT_REPORT` — the report, one TSV row per case (default
  `results/testkit-dotnet.tsv`). `missing-apis-dotnet.md` lands beside it: the checks the
  HTTP protocol cannot express (an error code or SQLSTATE), which are recorded, not failed;
- `FROSTLAKE_TESTKIT_FILTER` — replay only the suites whose name contains this text.

Every case gets a connection of its own, reset to an empty `test_db.test_schema`, and its
cells are read through the provider's typed values, then compared after the corpus's
normalization. A case whose skip clause names `dotnet` or `http` is skipped. The run ends
with `testkit [dotnet]: <P> passed, <F> failed, <S> skipped` and exits 1 when any case
failed or errored.

## Protocol

One `POST /api/execute` per statement with `{ sql, sessionId, requireSession, autoCommit }`;
the server issues the `sessionId` on first contact and the connection echoes it back, so
session state (current database/schema, transactions) persists across statements (see
[Session lifetime](#session-lifetime)). `GET /api/health` backs `Open()`'s reachability check.

DML statements answer a single-row result whose count columns are named "number of
rows …" — one column for INSERT/DELETE, but UPDATE always carries a second (always-zero)
"number of multi-joined rows updated" column and MERGE one column per action.
`ExecuteNonQuery` and `RecordsAffected` sum every "number of rows …" column, the same
rule the engine's own JDBC driver applies, so an UPDATE reports its count and a MERGE
the total across its actions. DDL statements answer a one-row Snowflake-style status
message on 0.1.0+ engines ("Table T successfully created."; older engines answer
nothing); either way `ExecuteNonQuery` reports -1 for them, per ADO.NET convention.
`ExecuteScalar` returns `DBNull.Value` for a NULL first cell and null only when there is
no row at all.

`Open()` applies the DSN's database and schema eagerly, so an unknown database fails at
`Open()` rather than on the first statement.

### Timeouts and async

`CommandTimeout` is honoured per statement (seconds; `0`, the default, waits
indefinitely) and a lapsed deadline raises `FrostlakeException`. The async surface —
`OpenAsync`, `ExecuteNonQueryAsync`, `ExecuteScalarAsync`, `ExecuteReaderAsync` — goes
over the wire asynchronously rather than blocking a pool thread, and honours its
`CancellationToken`.

### Session lifetime

The engine names the session in its answer to the connection's first statement (the `USE
DATABASE` of `Open()` when the connection string names a database), and every later request
names it back. That first answer also says what the engine supports. An engine that reports
`newSession` (0.1.0 and later) is sent `requireSession: true` on every request that names the
session, so a session it no longer holds — idle for 30 minutes, released, or lost to a
restart — is refused with a 404 and nothing runs. Without it the engine would run the
statement in a fresh session at its default database. An older engine is sent neither
`requireSession` nor a `DELETE`.

After that refusal the connection drops the session and:

- when the lost session held an open transaction — from `BeginTransaction()` or a SQL
  `BEGIN` / `START TRANSACTION` — it throws `FrostlakeSessionLostException` (a
  `FrostlakeException`): the transaction is gone and the statement did not run. The
  `FrostlakeTransaction` is marked lost, so `Commit()` and `Rollback()` throw that exception
  again and disposing it sends nothing;
- when a statement had moved the session's context — `USE` (so `ChangeDatabase` and
  `ChangeSchema` too), `SET` / `UNSET`, `ALTER SESSION`, a temporary object, or a `CREATE` /
  `DROP` of a database or schema — it throws the same exception: the context went with the
  session, and the statement was not re-run;
- otherwise it starts a fresh session on the connection string's database and schema, with
  the same `USE DATABASE` / `USE SCHEMA` that `Open()` sends, and sends the statement once
  more. A second refusal throws.

Either way the connection stays open, and its next statement starts a fresh session on the
connection string's database and schema.

`Close()` (and so `Dispose()`) releases the engine session with `DELETE /api/sessions/{id}`,
and the engine rolls back a transaction still open on it — one a SQL `BEGIN` opened as much
as one from `BeginTransaction()`, as ADO.NET expects of `Close()`. The release is sent once,
waits at most 10 seconds, and never throws; closing again sends nothing. An `Open()` whose
database or schema the engine refuses releases the session that refusal started. An engine
before 0.1.0 has no release endpoint: closing sends it a `ROLLBACK` when a transaction is
open, and the session lives on until the server's own idle timeout reclaims it. (There is no
connection pooling, and the protocol carries no credentials, so authentication is not
available over this transport.)
