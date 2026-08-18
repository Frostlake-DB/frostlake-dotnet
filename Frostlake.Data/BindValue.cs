namespace Frostlake.Data;

/// <summary>One bind value handed to <see cref="SqlSubstitution"/>: an optional name plus the value.</summary>
internal sealed class BindValue
{
    public BindValue(string? name, object? value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>The parameter's name without any <c>@</c>/<c>:</c> prefix, or null when it was supplied positionally.</summary>
    public string? Name { get; }

    public object? Value { get; }
}
