using System;
using System.Collections.Generic;
using System.Linq;

namespace RamuhLoop;

internal static class AdditionalItems
{
	internal static readonly int[] DefaultIds = new int[14]
	{
		8341, 8342, 8343, 8344, 8345, 8346, 8347, 8348, 9233, 10450,
		10512, 10574, 20376, 20377
	};

	internal static bool TryAdd(List<int> ids, int id)
	{
		if (id <= 0 || ids.Contains(id))
		{
			return false;
		}
		ids.Add(id);
		return true;
	}

	internal static T[] Select<T>(IEnumerable<T> slots, IEnumerable<int> ids, Func<T, int> container, Func<T, uint> itemId, Func<T, int> quantity)
	{
		HashSet<uint> allowed = (from x in ids
			where x > 0
			select (uint)x).ToHashSet();
		return slots.Where((T x) =>
		{
			int num = container(x);
			return num >= 0 && num <= 3 && quantity(x) == 1 && allowed.Contains(itemId(x));
		}).ToArray();
	}

	internal static bool Completed(uint currentSlotId, uint targetId, int previousCount, int currentCount)
	{
		if (currentSlotId != targetId && previousCount > 0)
		{
			return currentCount == previousCount - 1;
		}
		return false;
	}
}
