using System.Collections;
using System.Data.Common;

namespace Frostlake.Data;

/// <summary>A plain ordered list of <see cref="FrostlakeParameter"/>s.</summary>
public sealed class FrostlakeParameterCollection : DbParameterCollection
{
    private readonly List<FrostlakeParameter> _parameters = new();

    public override int Count => _parameters.Count;

    public override object SyncRoot => _parameters;

    public override int Add(object value)
    {
        _parameters.Add(Cast(value));
        return _parameters.Count - 1;
    }

    public FrostlakeParameter AddWithValue(string parameterName, object? value)
    {
        var parameter = new FrostlakeParameter(parameterName, value);
        _parameters.Add(parameter);
        return parameter;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear()
    {
        _parameters.Clear();
    }

    public override bool Contains(object value)
    {
        return _parameters.Contains(Cast(value));
    }

    public override bool Contains(string value)
    {
        return IndexOf(value) >= 0;
    }

    public override void CopyTo(Array array, int index)
    {
        ((ICollection)_parameters).CopyTo(array, index);
    }

    public override IEnumerator GetEnumerator()
    {
        return _parameters.GetEnumerator();
    }

    public override int IndexOf(object value)
    {
        return _parameters.IndexOf(Cast(value));
    }

    public override int IndexOf(string parameterName)
    {
        for (var i = 0; i < _parameters.Count; i++)
        {
            if (string.Equals(_parameters[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    public override void Insert(int index, object value)
    {
        _parameters.Insert(index, Cast(value));
    }

    public override void Remove(object value)
    {
        _parameters.Remove(Cast(value));
    }

    public override void RemoveAt(int index)
    {
        _parameters.RemoveAt(index);
    }

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
        {
            _parameters.RemoveAt(index);
        }
    }

    protected override DbParameter GetParameter(int index)
    {
        return _parameters[index];
    }

    protected override DbParameter GetParameter(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new FrostlakeException($"no parameter named {parameterName}");
        }
        return _parameters[index];
    }

    protected override void SetParameter(int index, DbParameter value)
    {
        _parameters[index] = Cast(value);
    }

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            _parameters.Add(Cast(value));
        }
        else
        {
            _parameters[index] = Cast(value);
        }
    }

    /// <summary>
    /// The parameters as bind values. A name is kept for <c>@name</c>/<c>:name</c> lookup with any
    /// prefix stripped; an unnamed parameter binds positionally to the next <c>?</c>.
    /// </summary>
    internal IReadOnlyList<BindValue> Binds()
    {
        var binds = new List<BindValue>(_parameters.Count);
        foreach (var parameter in _parameters)
        {
            binds.Add(new BindValue(NameOf(parameter), parameter.Value));
        }
        return binds;
    }

    private static string? NameOf(FrostlakeParameter parameter)
    {
        var name = parameter.ParameterName;
        if (name.Length > 0 && (name[0] == '@' || name[0] == ':'))
        {
            name = name[1..];
        }
        return name.Length > 0 ? name : null;
    }

    private static FrostlakeParameter Cast(object value)
    {
        return value as FrostlakeParameter
            ?? throw new FrostlakeException($"expected FrostlakeParameter, got {value?.GetType().Name ?? "null"}");
    }
}
