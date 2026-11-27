using System;
using System.Runtime.InteropServices;

namespace RamuhLoop;

internal sealed class CatSound : IDisposable
{
	private const uint SoundAsync = 1u;

	private const uint SoundNodefault = 2u;

	private const uint SoundMemory = 4u;

	private readonly byte[] wav = BuildWav();

	private GCHandle pinned;

	[DllImport("winmm.dll", SetLastError = true)]
	private static extern bool PlaySound(nint sound, nint module, uint flags);

	public CatSound()
	{
		pinned = GCHandle.Alloc(wav, GCHandleType.Pinned);
	}

	public void Play()
	{
		PlaySound(pinned.AddrOfPinnedObject(), IntPtr.Zero, 7u);
	}

	public void Dispose()
	{
		PlaySound(IntPtr.Zero, IntPtr.Zero, 0u);
		if (pinned.IsAllocated)
		{
			pinned.Free();
		}
	}

	private static byte[] BuildWav()
	{
		int num = 15876;
		byte[] array = new byte[44 + num * 2];
		WriteAscii(array, 0, "RIFF");
		WriteInt(array, 4, array.Length - 8);
		WriteAscii(array, 8, "WAVEfmt ");
		WriteInt(array, 16, 16);
		WriteShort(array, 20, 1);
		WriteShort(array, 22, 1);
		WriteInt(array, 24, 22050);
		WriteInt(array, 28, 44100);
		WriteShort(array, 32, 2);
		WriteShort(array, 34, 16);
		WriteAscii(array, 36, "data");
		WriteInt(array, 40, num * 2);
		double num2 = 0.0;
		for (int i = 0; i < num; i++)
		{
			double num3 = (double)i / 22050.0;
			double num4 = num3 / 0.72;
			double num5 = ((num4 < 0.34) ? (310.0 + 430.0 * num4 / 0.34) : (740.0 - 390.0 * (num4 - 0.34) / 0.66));
			num2 += Math.PI * 2.0 * num5 / 22050.0;
			double num6 = Math.Min(1.0, num3 / 0.055);
			double num7 = Math.Min(1.0, (0.72 - num3) / 0.16);
			double num8 = 0.86 + 0.14 * Math.Sin(Math.PI * 48.0 * num3);
			short value = (short)(Math.Clamp((Math.Sin(num2) + 0.42 * Math.Sin(num2 * 2.02) + 0.17 * Math.Sin(num2 * 3.01)) / 1.59 * num6 * num7 * num8, -1.0, 1.0) * 18500.0);
			WriteShort(array, 44 + i * 2, value);
		}
		return array;
	}

	private static void WriteAscii(byte[] data, int offset, string value)
	{
		for (int i = 0; i < value.Length; i++)
		{
			data[offset + i] = (byte)value[i];
		}
	}

	private static void WriteInt(byte[] data, int offset, int value)
	{
		data[offset] = (byte)value;
		data[offset + 1] = (byte)(value >> 8);
		data[offset + 2] = (byte)(value >> 16);
		data[offset + 3] = (byte)(value >> 24);
	}

	private static void WriteShort(byte[] data, int offset, int value)
	{
		data[offset] = (byte)value;
		data[offset + 1] = (byte)(value >> 8);
	}
}
