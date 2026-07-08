namespace DeadAir.Core;

/// <summary>Fixed-size rolling buffer of waveform samples (oldest→newest).</summary>
public sealed class WaveformRingBuffer(int capacity)
{
    private readonly double[] _values = new double[capacity];

    public IReadOnlyList<double> Values => _values;

    public void PushRange(IReadOnlyList<double> values)
    {
        foreach (var v in values)
        {
            Array.Copy(_values, 1, _values, 0, _values.Length - 1);
            _values[^1] = v;
        }
    }

    public void Reset() => Array.Clear(_values);
}
