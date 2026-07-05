namespace DeadAir.Core;

/// <summary>Fixed-size rolling buffer of display levels (oldest→newest).</summary>
public sealed class LevelRingBuffer(int size)
{
    private readonly double[] _values = new double[size];

    public IReadOnlyList<double> Values => _values;

    public void Push(double v)
    {
        Array.Copy(_values, 1, _values, 0, _values.Length - 1);
        _values[^1] = v;
    }

    public void Reset() => Array.Clear(_values);
}
