using Xunit;

namespace Frostlake.Data.Tests;

/// <summary>How a request's SQL is read for what it leaves on the session. No engine required.</summary>
public class SessionEffectsTests
{
    [Fact]
    public void ARequestSplitsOnItsTopLevelSemicolonsOnly()
    {
        Assert.Equal(
            new[] { "SELECT 1", " SELECT ';'", " SELECT \";\"" },
            SessionEffects.Statements("SELECT 1; SELECT ';'; SELECT \";\";"));
        Assert.Single(SessionEffects.Statements("EXECUTE IMMEDIATE $$ SELECT 1; SELECT 2; $$"));
        Assert.Equal(2, SessionEffects.Statements("SELECT 1 -- a; b\n; SELECT 2").Count);
        Assert.Equal(2, SessionEffects.Statements("SELECT 'it''s; \\' ;'; SELECT 2").Count);
        Assert.Empty(SessionEffects.Statements(" ; ;"));
    }

    [Fact]
    public void LeadingWordsSkipCommentsAndFoldCase()
    {
        Assert.Equal(
            new[] { "CREATE", "OR", "REPLACE" },
            SessionEffects.LeadingWords("/* c */ -- x\n create or replace table t", 3));
    }

    [Theory]
    [InlineData("USE SCHEMA s", true)]
    [InlineData("use database d", true)]
    [InlineData("SET x = 1", true)]
    [InlineData("UNSET x", true)]
    [InlineData("ALTER SESSION SET TIMEZONE = 'UTC'", true)]
    [InlineData("ALTER SESSION UNSET TIMEZONE", true)]
    [InlineData("CREATE OR REPLACE DATABASE d", true)]
    [InlineData("CREATE SCHEMA IF NOT EXISTS s", true)]
    [InlineData("DROP DATABASE d", true)]
    [InlineData("DROP SCHEMA IF EXISTS s", true)]
    [InlineData("CREATE TEMPORARY TABLE t (a INT)", true)]
    [InlineData("CREATE OR REPLACE TEMP TABLE t (a INT)", true)]
    [InlineData("CREATE LOCAL TEMPORARY TABLE t (a INT)", true)]
    [InlineData("/* lead */ use role r", true)]
    [InlineData("CREATE TABLE t (a INT)", false)]
    [InlineData("CREATE OR REPLACE TRANSIENT TABLE t (a INT)", false)]
    [InlineData("DROP TABLE t", false)]
    [InlineData("ALTER TABLE t ADD COLUMN b INT", false)]
    [InlineData("SELECT 1", false)]
    [InlineData("INSERT INTO t VALUES (1)", false)]
    [InlineData("SELECT 'USE SCHEMA s'", false)]
    public void WhichStatementsLeaveSessionStateBehind(string sql, bool expected)
    {
        Assert.Equal(expected, SessionEffects.TouchesSession(sql));
    }

    [Theory]
    [InlineData("BEGIN", "Begins")]
    [InlineData("begin transaction", "Begins")]
    [InlineData("BEGIN WORK", "Begins")]
    [InlineData("BEGIN NAME t1", "Begins")]
    [InlineData("START TRANSACTION", "Begins")]
    [InlineData("COMMIT", "Ends")]
    [InlineData("ROLLBACK WORK", "Ends")]
    [InlineData("BEGIN LET x := 1; RETURN x; END", "None")]
    [InlineData("SELECT 1", "None")]
    public void TransactionControlIsRecognisedAndAScriptingBlockIsNot(string sql, string expected)
    {
        var effect = SessionEffects.TransactionEffectOf(SessionEffects.Statements(sql)[0]);
        Assert.Equal(expected, effect.ToString());
    }
}
