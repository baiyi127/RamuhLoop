using System.Collections.Generic;
using Dalamud.Configuration;

namespace RamuhLoop;

public sealed class Configuration : IPluginConfiguration
{
	public int WhiteMage = 36;

	public int Paladin;

	public int Macro = -1;

	public bool SharedMacro;

	public int CofferId;

	public int SwordId;

	public int ShieldId;

	public int MaxLoops = 1;

	public int MacroDelay = 5;

	public int DutyTimeout = 600;

	public int DesynthDelaySeconds = 2;

	public bool DesynthAdditionalItems = true;

	public bool MouseClickMeow = true;

	public bool AutoRepair = true;

	public List<int> AdditionalItemIds = new List<int>(AdditionalItems.DefaultIds);

	public int Version { get; set; } = 1;
}
