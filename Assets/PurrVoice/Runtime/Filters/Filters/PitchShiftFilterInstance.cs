using System;

namespace PurrNet.Voice
{
    public class PitchShiftFilterInstance : FilterInstance
    {
        private readonly PitchShiftFilter _def;

        private float[] _buffer;
        private int _bufferSize;
        private int _writePos;
        private double _readPos1;
        private double _readPos2;

        private bool _initialized;
        private int _lastFrequency;

        public PitchShiftFilterInstance(PitchShiftFilter def) => _def = def;

        public override void Process(ArraySegment<float> inputSamples, int frequency, float strength)
        {
            float semitones = _def.semitones * strength;

            if (Math.Abs(semitones) < 0.001f)
                return;

            if (!_initialized || frequency != _lastFrequency)
                Initialize(frequency);

            double pitchRatio = Math.Pow(2.0, semitones / 12.0);

            float[] arr = inputSamples.Array;
            int off = inputSamples.Offset;
            int count = inputSamples.Count;

            for (int i = 0; i < count; i++)
            {
                _buffer[_writePos] = arr[off + i];

                float s1 = ReadSample(_readPos1);
                float s2 = ReadSample(_readPos2);

                double t1 = ((_readPos1 - _writePos) % _bufferSize + _bufferSize) % _bufferSize / _bufferSize;
                double t2 = ((_readPos2 - _writePos) % _bufferSize + _bufferSize) % _bufferSize / _bufferSize;

                float fade1 = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * t1));
                float fade2 = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * t2));

                float total = fade1 + fade2;
                if (total > 1e-6f)
                {
                    fade1 /= total;
                    fade2 /= total;
                }

                arr[off + i] = (float)Math.Clamp(s1 * fade1 + s2 * fade2, -1.0, 1.0);

                _writePos = (_writePos + 1) % _bufferSize;
                _readPos1 = (_readPos1 + pitchRatio) % _bufferSize;
                _readPos2 = (_readPos2 + pitchRatio) % _bufferSize;
            }
        }

        private void Initialize(int frequency)
        {
            _bufferSize = NextPowerOf2(frequency / 20);
            if (_bufferSize < 1024) _bufferSize = 1024;
            if (_bufferSize > 8192) _bufferSize = 8192;

            _buffer = new float[_bufferSize];
            _writePos = 0;
            _readPos1 = 0;
            _readPos2 = _bufferSize / 2.0;
            _lastFrequency = frequency;
            _initialized = true;
        }

        private float ReadSample(double position)
        {
            int pos0 = (int)position;
            float frac = (float)(position - pos0);
            pos0 %= _bufferSize;
            if (pos0 < 0) pos0 += _bufferSize;
            int pos1 = (pos0 + 1) % _bufferSize;
            return _buffer[pos0] * (1f - frac) + _buffer[pos1] * frac;
        }

        private static int NextPowerOf2(int v)
        {
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return v + 1;
        }
    }
}
