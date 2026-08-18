# frostlake-dotnet

An ADO.NET provider for [Frostlake](https://frostlake.dev), speaking the engine's HTTP
protocol against a running `DatabaseHttpServer`. .NET 8, zero runtime dependencies —
transport is `HttpClient`, parsing is `System.Text.Json`, both in the BCL. Because it
implements the standard `System.Data.Common` surface, anything that rides on ADO.NET
(Dapper included) works on top.

## Engine version

Requires a Frostlake engine **0.0.7 or newer**. Ask a running server which one it is with
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
transaction rolls back). Multi-statement responses are walked with `reader.NextResult()`.
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
the marker does not continue an identifier, and `::` is always read as the cast operator.
So stage references (`COPY INTO t FROM @my_stage`), VARIANT paths (`v:address:city`),
casts (`v::date`) and scripting assignment (`LET x := 1`) pass through untouched even
when a parameter happens to share the name.

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
`bool`, `DATE`/`TIMESTAMP*` → `DateTime`, `TIME` → `TimeSpan`, `BINARY` → `byte[]`,
everything else — including `VARIANT`/`OBJECT`/`ARRAY` as their JSON text — `string`.

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

## Protocol

One `POST /api/execute` per statement with `{ sql, sessionId, autoCommit }`; the server
issues the `sessionId` on first contact and the connection echoes it back, so session
state (current database/schema, transactions) persists across statements. `GET
/api/health` backs `Open()`'s reachability check. DML statements answer a one-cell
"number of rows …" result, which `ExecuteNonQuery` returns as the update count.

`Open()` applies the DSN's database and schema eagerly, so an unknown database fails at
`Open()` rather than on the first statement.

### Timeouts and async

`CommandTimeout` is honoured per statement (seconds; `0`, the default, waits
indefinitely) and a lapsed deadline raises `FrostlakeException`. The async surface —
`OpenAsync`, `ExecuteNonQueryAsync`, `ExecuteScalarAsync`, `ExecuteReaderAsync` — goes
over the wire asynchronously rather than blocking a pool thread, and honours its
`CancellationToken`.

### Session lifetime

The server offers no endpoint for dropping a session, so `Close()` can only release the
client's handle: the session lives on until the server's own idle timeout reclaims it.
Hold a connection open rather than cycling open/close in a loop. (There is no connection
pooling, and the protocol carries no credentials — `SqlRequest` is `{ sql, sessionId,
autoCommit }` — so authentication is not available over this transport.)
