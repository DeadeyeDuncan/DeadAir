namespace DeadAir.Core;

/// <summary>Fixed-size rolling buffer of waveform samples (oldest→newest).</summary>
public sealed class WaveformRingBuffer(int capacity)
{
    private readonly double[] _values = new double[capacity];

    public IReadOnlyList<double> Values => _values;

    public void PushRange(IReadOnlyList<double> values)
    {
        int m = values.Count;
        if (m == 0) return;
        int cap = _values.Length;

        if (m >= cap)
        {
            // The batch fills or overflows the buffer: keep only its last `cap`.
            for (int i = 0; i < cap; i++) _values[i] = values[m - cap + i];
            return;
        }

        // Shift the survivors left by m in one copy, then append the m newest.
        Array.Copy(_values, m, _values, 0, cap - m);
        for (int i = 0; i < m; i++) _values[cap - m + i] = values[i];
    }

    public void Reset() => Array.Clear(_values);
}
