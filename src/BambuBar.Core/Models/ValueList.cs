namespace BambuBar.Models;

/// <summary>
/// A list that compares by its items, so records holding one compare by value the way Swift's
/// <c>Equatable</c> structs with arrays do.
/// </summary>
public sealed class ValueList<T> : List<T>, IEquatable<ValueList<T>>
{
    public ValueList() { }
    public ValueList(IEnumerable<T> items) : base(items) { }

    public bool Equals(ValueList<T>? other) => other is not null && this.SequenceEqual(other);
    public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in this) hash.Add(item);
        return hash.ToHashCode();
    }

    public static bool operator ==(ValueList<T>? a, ValueList<T>? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(ValueList<T>? a, ValueList<T>? b) => !(a == b);
}

public static class ValueListExtensions
{
    public static ValueList<T> ToValueList<T>(this IEnumerable<T> items) => new(items);
}
