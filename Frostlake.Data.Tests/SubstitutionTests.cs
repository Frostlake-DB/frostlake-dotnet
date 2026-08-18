using Xunit;

namespace Frostlake.Data.Tests;

public class SubstitutionTests
{
    [Fact]
    public void SkipsLiteralsIdentifiersAndComments()
    {
        var rendered = SqlSubstitution.Substitute(
            "SELECT 'a?b', \"c?d\", ? -- e?f\n, ? /* g?h */",
            new object?[] { "x", 2 });
        Assert.Equal("SELECT 'a?b', \"c?d\", 'x' -- e?f\n, 2 /* g?h */", rendered);
    }

    [Fact]
    public void SkipsLineCommentsWrittenWithSlashes()
    {
        var rendered = SqlSubstitution.Substitute("SELECT ? // not ? here\n", new object?[] { 1 });
        Assert.Equal("SELECT 1 // not ? here\n", rendered);
    }

    [Fact]
    public void EncodesBackslashesThenQuotes()
    {
        var rendered = SqlSubstitution.Substitute("SELECT ?", new object?[] { "Ada O'Hara \\ Byron" });
        Assert.Equal("SELECT 'Ada O''Hara \\\\ Byron'", rendered);
    }

    [Fact]
    public void FormatsTypedLiterals()
    {
        Assert.Equal("NULL", SqlSubstitution.FormatLiteral(null));
        Assert.Equal("NULL", SqlSubstitution.FormatLiteral(DBNull.Value));
        Assert.Equal("TRUE", SqlSubstitution.FormatLiteral(true));
        Assert.Equal("FALSE", SqlSubstitution.FormatLiteral(false));
        Assert.Equal("9.5", SqlSubstitution.FormatLiteral(9.5));
        Assert.Equal("9.5", SqlSubstitution.FormatLiteral(9.5f));
        Assert.Equal("9.50", SqlSubstitution.FormatLiteral(9.50m));
        Assert.Equal("7", SqlSubstitution.FormatLiteral(7));
        Assert.Equal("'c'", SqlSubstitution.FormatLiteral('c'));
        Assert.Equal("X'CAFE'", SqlSubstitution.FormatLiteral(new byte[] { 0xCA, 0xFE }));
        Assert.Equal(
            "'2026-01-02T03:04:05.0000000'::TIMESTAMP_NTZ",
            SqlSubstitution.FormatLiteral(new DateTime(2026, 1, 2, 3, 4, 5)));
        Assert.Equal("'2026-01-02'::DATE", SqlSubstitution.FormatLiteral(new DateOnly(2026, 1, 2)));
        Assert.Equal("'03:04:05.0000000'::TIME", SqlSubstitution.FormatLiteral(new TimeOnly(3, 4, 5)));
        Assert.Equal("'03:04:05.0000000'::TIME", SqlSubstitution.FormatLiteral(new TimeSpan(3, 4, 5)));
        Assert.Equal("[1, 'a']", SqlSubstitution.FormatLiteral(new object?[] { 1, "a" }));
        Assert.Contains("::TIMESTAMP_TZ", SqlSubstitution.FormatLiteral(DateTimeOffset.UtcNow));
        Assert.Equal("'00000000-0000-0000-0000-000000000000'", SqlSubstitution.FormatLiteral(Guid.Empty));
    }

    [Fact]
    public void RejectsNonFiniteAndUnsupportedValues()
    {
        Assert.Throws<FrostlakeException>(() => SqlSubstitution.FormatLiteral(double.NaN));
        Assert.Throws<FrostlakeException>(() => SqlSubstitution.FormatLiteral(float.PositiveInfinity));
        Assert.Throws<FrostlakeException>(() => SqlSubstitution.FormatLiteral(new object()));
    }

    // --- the bugs this file exists to pin down -------------------------------------------------

    [Fact]
    public void DollarQuotedBodiesAreLeftAlone()
    {
        var rendered = SqlSubstitution.Substitute(
            "CREATE PROCEDURE p() AS $$ if (x ? 1 : 2) $$ SELECT ?",
            new object?[] { 99 });
        Assert.Equal("CREATE PROCEDURE p() AS $$ if (x ? 1 : 2) $$ SELECT 99", rendered);
    }

    [Fact]
    public void UnterminatedDollarQuoteConsumesTheRest()
    {
        var rendered = SqlSubstitution.Substitute("SELECT ?, $$ tail ?", new object?[] { 1 });
        Assert.Equal("SELECT 1, $$ tail ?", rendered);
    }

    [Fact]
    public void SurplusBindValuesAreRejected()
    {
        // Silently dropping them used to re-send a stale value and write the wrong row.
        var error = Assert.Throws<FrostlakeException>(
            () => SqlSubstitution.Substitute("SELECT ?", new object?[] { 1, 2, 3 }));
        Assert.Contains("2 positional bind value(s) left over", error.Message);
    }

    [Fact]
    public void MissingBindValuesAreRejected()
    {
        Assert.Throws<FrostlakeException>(() => SqlSubstitution.Substitute("SELECT ?, ?", new object?[] { 1 }));
    }

    [Fact]
    public void UnterminatedStringLiteralDoesNotCrash()
    {
        Assert.Equal(@"SELECT 'abc\", SqlSubstitution.Substitute(@"SELECT 'abc\", new object?[] { }));
        Assert.Equal("SELECT 'abc", SqlSubstitution.Substitute("SELECT 'abc", new object?[] { }));
        Assert.Equal("SELECT \"abc", SqlSubstitution.Substitute("SELECT \"abc", new object?[] { }));
        Assert.Equal("SELECT /* abc", SqlSubstitution.Substitute("SELECT /* abc", new object?[] { }));
    }

    [Fact]
    public void NamedParametersBindByName()
    {
        var binds = new[] { new BindValue("id", 7), new BindValue("name", "Ada") };
        Assert.Equal(
            "SELECT * FROM t WHERE id = 7 AND name = 'Ada'",
            SqlSubstitution.Substitute("SELECT * FROM t WHERE id = @id AND name = @name", binds));
        Assert.Equal(
            "SELECT * FROM t WHERE id = 7",
            SqlSubstitution.Substitute("SELECT * FROM t WHERE id = :id", binds));
    }

    [Fact]
    public void NamedParametersMayRepeat()
    {
        var binds = new[] { new BindValue("id", 7) };
        Assert.Equal("SELECT 7, 7, 7", SqlSubstitution.Substitute("SELECT @id, @id, @id", binds));
        Assert.Equal("SELECT 7 + 7", SqlSubstitution.Substitute("SELECT @id + @id", binds));
    }

    [Fact]
    public void UnknownAtNamesAreLeftAloneSoStageReferencesSurvive()
    {
        var binds = new[] { new BindValue("id", 7) };
        Assert.Equal(
            "COPY INTO t FROM @my_stage WHERE id = 7",
            SqlSubstitution.Substitute("COPY INTO t FROM @my_stage WHERE id = @id", binds));
    }

    [Fact]
    public void VariantPathsAreNotMistakenForParameters()
    {
        var binds = new[] { new BindValue("id", 7) };
        Assert.Equal(
            "SELECT v:name, v:address:city FROM t WHERE id = 7",
            SqlSubstitution.Substitute("SELECT v:name, v:address:city FROM t WHERE id = @id", binds));
    }

    [Fact]
    public void NamedAndPositionalCanMix()
    {
        var binds = new[] { new BindValue(null, "x"), new BindValue("id", 7) };
        Assert.Equal("SELECT 'x' WHERE id = 7", SqlSubstitution.Substitute("SELECT ? WHERE id = @id", binds));
    }

    [Fact]
    public void CastOperatorIsNeverMistakenForABind()
    {
        // A parameter named like a type must not eat the :: cast.
        var binds = new[] { new BindValue("date", "X"), new BindValue("id", 1) };
        Assert.Equal(
            "SELECT v::date FROM t WHERE id = 1",
            SqlSubstitution.Substitute("SELECT v::date FROM t WHERE id = @id", binds));
        Assert.Equal(
            "SELECT '2026-01-01'::TIMESTAMP_NTZ, x::NUMBER(10,2)",
            SqlSubstitution.Substitute(
                "SELECT '2026-01-01'::TIMESTAMP_NTZ, x::NUMBER(10,2)",
                new[] { new BindValue("TIMESTAMP_NTZ", "boom") }));
    }

    [Fact]
    public void VariantPathsSurviveEvenWhenAParameterSharesTheName()
    {
        var binds = new[] { new BindValue("a", "nope"), new BindValue("id", 1) };
        Assert.Equal(
            "SELECT v:a:b, 1",
            SqlSubstitution.Substitute("SELECT v:a:b, @id", binds));
    }

    [Fact]
    public void NamedParametersThatMatchNothingAreStillFine()
    {
        // Every named parameter may go unused - a statement need not reference any of them.
        Assert.Equal(
            "COPY INTO t FROM @my_stage",
            SqlSubstitution.Substitute("COPY INTO t FROM @my_stage", new[] { new BindValue("other", 1) }));
        Assert.Equal("SELECT 1", SqlSubstitution.Substitute("SELECT 1", new[] { new BindValue("id", 1) }));
    }

    [Fact]
    public void StrandedPositionalValuesAreStillRejected()
    {
        var error = Assert.Throws<FrostlakeException>(
            () => SqlSubstitution.Substitute("SELECT 1", new object?[] { 1 }));
        Assert.Contains("positional bind value", error.Message);
        Assert.Throws<FrostlakeException>(
            () => SqlSubstitution.Substitute("SELECT @id", new[] { new BindValue(null, 1), new BindValue("id", 2) }));
    }

    [Fact]
    public void LoneMarkersAreLeftAlone()
    {
        Assert.Equal("SELECT @", SqlSubstitution.Substitute("SELECT @", new[] { new BindValue("id", 1) }));
        Assert.Equal("SELECT :", SqlSubstitution.Substitute("SELECT :", new[] { new BindValue("id", 1) }));
        Assert.Equal("LET x := 8", SqlSubstitution.Substitute("LET x := @id", new[] { new BindValue("id", 8) }));
        Assert.Equal("SELECT $1, 2", SqlSubstitution.Substitute("SELECT $1, @id", new[] { new BindValue("id", 2) }));
    }

    [Fact]
    public void NamedLookupIgnoresCase()
    {
        Assert.Equal("SELECT 4, 4, 4",
            SqlSubstitution.Substitute("SELECT @id, @ID, @Id", new[] { new BindValue("id", 4) }));
    }

    [Fact]
    public void DollarSignsInsideLiteralsDoNotOpenABody()
    {
        Assert.Equal("SELECT '$$ not a body ?', 1",
            SqlSubstitution.Substitute("SELECT '$$ not a body ?', ?", new object?[] { 1 }));
        Assert.Equal("SELECT $$a$$, $$b ? $$, 3",
            SqlSubstitution.Substitute("SELECT $$a$$, $$b ? $$, ?", new object?[] { 3 }));
        Assert.Equal("SELECT 9, $$ @id $$",
            SqlSubstitution.Substitute("SELECT @id, $$ @id $$", new[] { new BindValue("id", 9) }));
    }

    [Fact]
    public void UnusedNamedParametersAreTolerated()
    {
        // Dapper hands over every property of the parameter object, used or not.
        var binds = new[] { new BindValue("id", 7), new BindValue("unused", 1) };
        Assert.Equal("SELECT 7", SqlSubstitution.Substitute("SELECT @id", binds));
    }
}
