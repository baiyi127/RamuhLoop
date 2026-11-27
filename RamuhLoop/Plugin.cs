using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using RamuhLoop.Services;

namespace RamuhLoop;

public sealed class Plugin : IDalamudPlugin, IDisposable
{
	private enum Step
	{
		Stopped,
		WhiteMage,
		Queue,
		Enter,
		Macro,
		Exit,
		Paladin,
		Open,
		Opened,
		Salvage,
		Confirm,
		Salvaged,
		ScanExtras,
		Next,
		Repairing
	}

	private readonly record struct Slot(InventoryType Bag, int Index, uint ItemId, int Quantity);

	private readonly Configuration config;

	private readonly CatSound catSound = new CatSound();

	private readonly RepairService repairService = new RepairService();

	private readonly RepairCheckpoint repairCheckpoint;

	private int minimumEquipmentCondition = -1;

	private long nextConditionRead;

	private Step step;

	private long since;

	private long nextTick;

	private long nextCatAt;

	private long lastClickMeowAt;

	private bool leftMouseDown;

	private int loops;

	private uint dutyId;

	private ushort territory;

	private ulong character;

	private bool visible = true;

	private bool accepted;

	private bool macroOwned;

	private bool rareConfirmationSent;

	private string status = "请先设置配装、宏与物品。默认只运行一轮。";

	private string search = "拉姆";

	private List<Item> searchResults = new List<Item>();

	private List<Slot> before = new List<Slot>();

	private readonly Queue<Slot> rewards = new Queue<Slot>();

	private Slot target;

	private int coffersBefore;

	private long confirmReadySince;

	private long desynthReadySince;

	private long jobReadySince;

	private long nextJobAttempt;

	private int jobAttempts;

	private uint jobTerritory;

	private string lastOpenDiagnostic = "";

	private bool additionalScanDone;

	private int targetCountBefore;

	private int newAdditionalId;

	private string additionalMessage = "";

	private static readonly InventoryType[] Containers = new InventoryType[6]
	{
		InventoryType.Inventory1,
		InventoryType.Inventory2,
		InventoryType.Inventory3,
		InventoryType.Inventory4,
		InventoryType.ArmoryMainHand,
		InventoryType.ArmoryOffHand
	};

	[PluginService]
	internal static IDalamudPluginInterface PI { get; private set; } = null;

	[PluginService]
	internal static ICommandManager Commands { get; private set; } = null;

	[PluginService]
	internal static IClientState Client { get; private set; } = null;

	[PluginService]
	internal static IObjectTable Objects { get; private set; } = null;

	[PluginService]
	internal static IPlayerState Player { get; private set; } = null;

	[PluginService]
	internal static ICondition Conditions { get; private set; } = null;

	[PluginService]
	internal static IFramework Framework { get; private set; } = null;

	[PluginService]
	internal static IGameGui Gui { get; private set; } = null;

	[PluginService]
	internal static IDataManager Data { get; private set; } = null;

	[PluginService]
	internal static IDutyState Duty { get; private set; } = null;

	[PluginService]
	internal static IPluginLog Log { get; private set; } = null;

	[PluginService]
	internal static IChatGui Chat { get; private set; } = null;

	private bool Running => step != Step.Stopped;

	private static long Now => Environment.TickCount64;

	private long Elapsed => Now - since;

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int virtualKey);

	private void ScheduleCat()
	{
		nextCatAt = Now + 30000;
	}

	private void PlayCat()
	{
		catSound.Play();
		Chat.Print(new XivChatEntry
		{
			Type = XivChatType.TellIncoming,
			Name = "猫娘",
			Message = "喵喵",
			Silent = true
		});
	}

	public Plugin()
	{
		repairCheckpoint = new RepairCheckpoint(repairService);
		config = (PI.GetPluginConfig() as Configuration) ?? new Configuration();
		Configuration configuration = config;
		if (configuration.AdditionalItemIds == null)
		{
			configuration.AdditionalItemIds = new List<int>(AdditionalItems.DefaultIds);
		}
		Commands.AddHandler("/ramuhloop", new CommandInfo(OnCommand)
		{
			HelpMessage = "打开循环设置；stop 停止；start 开始"
		});
		Framework.Update += Update;
		PI.UiBuilder.Draw += Draw;
		PI.UiBuilder.OpenMainUi += Show;
		PI.UiBuilder.OpenConfigUi += Show;
	}

	private void Show()
	{
		visible = true;
	}

	private void OnCommand(string command, string args)
	{
		if (args.Trim() == "stop")
		{
			Stop("手动停止");
			return;
		}
		if (args.Trim() == "start")
		{
			Start();
			return;
		}
		if (args.Trim() == "inspect")
		{
			try
			{
				Chat.Print("[RamuhLoop] " + OpenDiagnostic(Snapshot()));
				return;
			}
			catch (Exception ex)
			{
				Chat.Print("[RamuhLoop] 背包诊断失败：" + ex.Message);
				return;
			}
		}
		Show();
	}

	private void Move(Step next)
	{
		step = next;
		since = Now;
		if ((next == Step.WhiteMage || next == Step.Paladin) ? true : false)
		{
			jobReadySince = 0L;
			nextJobAttempt = 0L;
			jobAttempts = 0;
			jobTerritory = Client.TerritoryType;
		}
		status = $"第 {loops + 1} 轮：{next}";
		Log.Information(status);
	}

	private unsafe void Stop(string reason)
	{
		repairCheckpoint.Reset();
		step = Step.Stopped;
		status = reason;
		rewards.Clear();
		if (macroOwned && Client.IsLoggedIn)
		{
			RaptureShellModule* ptr = RaptureShellModule.Instance();
			if (ptr != null)
			{
				ptr->CancelMacro();
			}
		}
		macroOwned = false;
		Chat.Print("[RamuhLoop] " + reason);
		Log.Information(reason);
	}

	private unsafe void Start()
	{
		if (Running)
		{
			return;
		}
		try
		{
			Require(Client.IsLoggedIn && Objects.LocalPlayer != null, "请先登录角色。");
			Require(!Conditions[ConditionFlag.BoundByDuty] && Ready(), "请在副本外空闲时开始。");
			int whiteMage = config.WhiteMage;
			int ok;
			if (whiteMage >= 1 && whiteMage <= 100)
			{
				whiteMage = config.Paladin;
				ok = ((whiteMage >= 1 && whiteMage <= 100) ? 1 : 0);
			}
			else
			{
				ok = 0;
			}
			Require((byte)ok != 0, "配装编号必须为 1–100。");
			whiteMage = config.Macro;
			Require(whiteMage >= 0 && whiteMage <= 99, "宏编号必须为游戏内显示的 0–99。");
			int ok2;
			if (config.MaxLoops >= 0)
			{
				whiteMage = config.MacroDelay;
				if (whiteMage >= 0 && whiteMage <= 120)
				{
					whiteMage = config.DutyTimeout;
					ok2 = ((whiteMage >= 60 && whiteMage <= 3600) ? 1 : 0);
					goto IL_0106;
				}
			}
			ok2 = 0;
			goto IL_0106;
			IL_0106:
			Require((byte)ok2 != 0, "循环/延时设置不正确。");
			whiteMage = config.DesynthDelaySeconds;
			Require(whiteMage >= 1 && whiteMage <= 10, "分解操作间隔必须为 1–10 秒。");
			RaptureGearsetModule* ptr = RaptureGearsetModule.Instance();
			Require(ptr != null, "无法读取配装。");
			RaptureGearsetModule.GearsetEntry* gearset = ptr->GetGearset(config.WhiteMage - 1);
			RaptureGearsetModule.GearsetEntry* gearset2 = ptr->GetGearset(config.Paladin - 1);
			Require(gearset != null && gearset->ClassJob == 24 && gearset2 != null && gearset2->ClassJob == 19, "指定配装的职业必须分别是白魔法师和骑士。");
			ContentFinderCondition[] array = (from x in Data.GetExcelSheet<ContentFinderCondition>()
				where x.TerritoryType.RowId == 375
				select x).ToArray();
			Require(array.Length == 1, "无法唯一确定拉姆歼殛战的排本行，请核对客户端数据。");
			string text = array[0].Name.ToString();
			Require(text.Contains("拉姆") || text.Contains("极拉姆") || text.Contains("極ラムウ") || text.Contains("Striking Tree", StringComparison.OrdinalIgnoreCase), "375 对应副本名称与拉姆不符。");
			dutyId = array[0].RowId;
			territory = (ushort)array[0].TerritoryType.RowId;
			Require(config.CofferId > 0 && config.SwordId > 0, "请选择武器箱与骑士剑的物品 ID。");
			Item row = Data.GetExcelSheet<Item>().GetRow((uint)config.CofferId);
			Require(row.ItemAction.RowId != 0 && row.Name.ToString().Length > 0, "指定的武器箱不是可使用物品。");
			ValidateWeapon((uint)config.SwordId, sword: true);
			if (config.ShieldId > 0)
			{
				ValidateWeapon((uint)config.ShieldId, sword: false);
			}
			if (config.DesynthAdditionalItems)
			{
				foreach (int item in config.AdditionalItemIds.Distinct())
				{
					ValidateAdditionalItem(item);
				}
			}
			Require(config.CofferId != config.SwordId && config.SwordId != config.ShieldId, "物品 ID 不能重复。");
			Require(ContentsFinder.Instance() != null && ContentsFinder.Instance()->GetQueueInfo() != null, "排本接口不可用。");
			Require(ContentsFinder.Instance()->GetQueueInfo()->QueueState == ContentsFinderQueueState.None, "请先退出现有排本队列。");
			Require(ContentsFinder.Instance()->IsUnrestrictedParty, "请先在任务搜索器勾选「解除人数限制」。");
			Require(!ContentsFinder.Instance()->IsLevelSync && !ContentsFinder.Instance()->IsExplorerMode, "请关闭等级同步与自由探索。");
			Require(!AddonVisible("SalvageDialog"), "请先关闭已打开的分解确认窗口。");
			PI.SavePluginConfig(config);
			character = Player.ContentId;
			loops = 0;
			repairCheckpoint.Reset();
			ScheduleCat();
			Move(Step.WhiteMage);
		}
		catch (Exception ex)
		{
			Stop(ex.Message);
		}
	}

	private static void Require(bool ok, string message)
	{
		if (!ok)
		{
			throw new InvalidOperationException(message);
		}
	}

	private static void ValidateWeapon(uint id, bool sword)
	{
		Item row = Data.GetExcelSheet<Item>().GetRow(id);
		Require(row.LevelItem.RowId == 100 && row.Desynth > 0 && row.ClassJobCategory.Value.PLD, $"物品 {id} 必须是品级 100、骑士可用、可分解的装备。");
		Require(sword ? (row.EquipSlotCategory.Value.MainHand != 0) : (row.EquipSlotCategory.Value.OffHand != 0), $"物品 {id} 的主手/副手类型不匹配。");
	}

	private static bool Ready(bool allowRepairCasting = false)
	{
		if (Objects.LocalPlayer != null && !Objects.LocalPlayer.IsDead && !Conditions[ConditionFlag.BetweenAreas] && !Conditions[ConditionFlag.BetweenAreas51] && !Conditions[ConditionFlag.OccupiedInCutSceneEvent] && !Conditions[ConditionFlag.WatchingCutscene] && (allowRepairCasting || !Conditions[ConditionFlag.Casting]))
		{
			return !Conditions[ConditionFlag.InCombat];
		}
		return false;
	}

	private unsafe void Update(IFramework framework)
	{
		if (Now >= nextConditionRead)
		{
			minimumEquipmentCondition = repairService.ReadMinimumCondition();
			nextConditionRead = Now + 1000;
		}
		bool flag = (GetAsyncKeyState(1) & 0x8000) != 0;
		if ((config.MouseClickMeow & flag) && !leftMouseDown && Now - lastClickMeowAt >= 150)
		{
			PlayCat();
			lastClickMeowAt = Now;
		}
		leftMouseDown = flag;
		if (!Running || Now < nextTick)
		{
			return;
		}
		nextTick = Now + 250;
		try
		{
			Require(Client.IsLoggedIn && Player.ContentId == character, "角色离线或切换，循环已停止。");
			Require(Objects.LocalPlayer == null || !Objects.LocalPlayer.IsDead, "角色死亡，循环已停止。");
			if (Now >= nextCatAt && Ready())
			{
				PlayCat();
				ScheduleCat();
			}
			bool flag2;
			switch (step)
			{
			case Step.WhiteMage:
			case Step.Enter:
			case Step.Exit:
			case Step.Paladin:
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			long num = (flag2 ? ((long)config.DutyTimeout * 1000L) : 45000);
			if (Elapsed >= num && step == Step.Opened)
			{
				throw new InvalidOperationException("开箱检测超时：" + OpenDiagnostic(Snapshot()));
			}
			Require(Elapsed < num, $"步骤 {step} 超时，请检查游戏提示。已停止，未重复发送动作。");
			if (step == Step.Repairing)
			{
				var finder = ContentsFinder.Instance();
				var queue = finder == null ? null : finder->GetQueueInfo();
				var context = new RepairCheckpointContext(
					Ready(allowRepairCasting: true), Conditions[ConditionFlag.BoundByDuty],
					queue != null && queue->QueueState == ContentsFinderQueueState.None,
					Objects.LocalPlayer == null ? 0u : Objects.LocalPlayer.ClassJob.RowId,
					Client.TerritoryType);
				var result = repairCheckpoint.Tick(context);
				if (result == WorkResult.Failed)
				{
					throw new InvalidOperationException(repairCheckpoint.Error);
				}
				if (result == WorkResult.Complete)
				{
					Move(repairCheckpoint.Resume == RepairResume.QueueDuty ? Step.Queue : Step.Open);
				}
				return;
			}
			if (!Ready())
			{
				jobReadySince = 0L;
				confirmReadySince = (desynthReadySince = 0L);
				return;
			}
			uint rowId = Objects.LocalPlayer.ClassJob.RowId;
			switch (step)
			{
			case Step.WhiteMage:
				UpdateJobSwitch(24u, config.WhiteMage, Step.Queue);
				break;
			case Step.Queue:
			{
				Require(rowId == 24 && !Conditions[ConditionFlag.BoundByDuty], "排本前职业或场景改变。");
				ContentsFinder* ptr5 = ContentsFinder.Instance();
				Require(ptr5 != null && ptr5->IsUnrestrictedParty && !ptr5->IsLevelSync, "请保持解除人数限制且关闭等级同步。");
				ContentsFinderQueueInfo* queueInfo2 = ptr5->GetQueueInfo();
				Require(queueInfo2 != null && queueInfo2->QueueState == ContentsFinderQueueState.None, "检测到已有排本，停止。");
				uint num3 = dutyId;
				queueInfo2->QueueDuties(&num3, 1, 0);
				accepted = false;
				Move(Step.Enter);
				break;
			}
			case Step.Enter:
			{
				if (Conditions[ConditionFlag.BoundByDuty])
				{
					Require(Client.TerritoryType == territory, "进入了非目标副本，停止。");
					Move(Step.Macro);
					break;
				}
				AddonContentsFinderConfirm* address = (AddonContentsFinderConfirm*)Gui.GetAddonByName("ContentsFinderConfirm").Address;
				ContentsFinderQueueInfo* queueInfo = ContentsFinder.Instance()->GetQueueInfo();
				if (!accepted && queueInfo != null && queueInfo->QueueState == ContentsFinderQueueState.Ready && queueInfo->PoppedQueueEntry.Id == dutyId && address != null && address->IsVisible)
				{
					accepted = Click(address->CommenceButton);
				}
				break;
			}
			case Step.Macro:
				Require(Client.TerritoryType == territory && rowId == 24, "宏执行前场景或职业不正确。");
				if (Duty.IsDutyStarted && Elapsed >= (long)config.MacroDelay * 1000L)
				{
					RaptureMacroModule* ptr2 = RaptureMacroModule.Instance();
					RaptureShellModule* ptr3 = RaptureShellModule.Instance();
					Require(ptr2 != null && ptr3 != null, "宏接口不可用。");
					Require(ptr3->MacroCurrentLine < 0, "已有其他宏运行。");
					RaptureMacroModule.Macro* macro = ptr2->GetMacro(config.SharedMacro ? 1u : 0u, (uint)config.Macro);
					Require(macro != null && ptr2->GetLineCount(macro) != 0, "指定宏为空。");
					ptr3->ExecuteMacro(macro);
					macroOwned = true;
					Move(Step.Exit);
				}
				break;
			case Step.Exit:
				if (!Conditions[ConditionFlag.BoundByDuty] && Client.TerritoryType != territory)
				{
					if (macroOwned)
					{
						RaptureShellModule.Instance()->CancelMacro();
						macroOwned = false;
					}
					Move(Step.Paladin);
				}
				break;
			case Step.Paladin:
				UpdateJobSwitch(19u, config.Paladin, Step.Open);
				break;
			case Step.Open:
			{
				Require(rowId == 19 && !Conditions[ConditionFlag.BoundByDuty], "开箱前职业或场景不正确。");
				Require(!AddonVisible("SalvageDialog"), "存在其他分解窗口，停止。");
				before = Snapshot();
				Require(!before.Any((Slot x) => x.ItemId == config.SwordId || (config.ShieldId > 0 && x.ItemId == config.ShieldId)), "背包或兵装库已有同名目标装备，请先移走，避免误分解。");
				Require(Enumerable.Count(before, (Slot x) => x.ItemId == 0 && (int)x.Bag < 4) >= 4, "背包至少需要四个空位。");
				Slot slot2 = before.FirstOrDefault((Slot x) => x.ItemId == config.CofferId && (int)x.Bag < 4);
				Require(slot2.ItemId != 0, "没有找到指定武器箱，循环停止。");
				coffersBefore = Count(before, (uint)config.CofferId);
				lastOpenDiagnostic = "";
				Log.Information("开箱前：" + OpenDiagnostic(before));
				AgentInventoryContext* ptr = AgentInventoryContext.Instance();
				Require(ptr != null, "背包接口不可用。");
				long value = ptr->UseItem(slot2.ItemId, InventoryType.Invalid, 0u, 0);
				Log.Information($"按物品 ID 使用箱子 {slot2.ItemId}，接口返回={value}；等待数量变化确认。");
				Move(Step.Opened);
				break;
			}
			case Step.Opened:
			{
				List<Slot> list = Snapshot();
				string text = OpenDiagnostic(list);
				if (text != lastOpenDiagnostic)
				{
					lastOpenDiagnostic = text;
					status = text;
					Log.Information("开箱检测：" + text);
				}
				if (Count(list, (uint)config.CofferId) == coffersBefore)
				{
					break;
				}
				Require(Count(list, (uint)config.CofferId) == coffersBefore - 1, "武器箱数量发生异常变化。");
				Slot[] array3 = list.Where((Slot x) => x.ItemId == config.SwordId || (config.ShieldId > 0 && x.ItemId == config.ShieldId)).ToArray();
				if (array3.Any((Slot x) => x.ItemId == config.SwordId) && (config.ShieldId <= 0 || array3.Any((Slot x) => x.ItemId == config.ShieldId)))
				{
					Require(array3.Length == ((config.ShieldId <= 0) ? 1 : 2) && array3.All((Slot x) => x.Quantity == 1), "开箱装备数量异常。");
					rewards.Clear();
					additionalScanDone = false;
					Slot[] array2 = array3;
					foreach (Slot item2 in array2)
					{
						rewards.Enqueue(item2);
					}
					Move(Step.Salvage);
				}
				break;
			}
			case Step.Salvage:
			{
				if (rewards.Count == 0)
				{
					Move((config.DesynthAdditionalItems && !additionalScanDone) ? Step.ScanExtras : Step.Next);
					break;
				}
				Require(rowId == 19 && !Conditions[ConditionFlag.BoundByDuty], "分解前职业或场景改变。");
				if (AddonVisible("SalvageResult"))
				{
					CloseResult();
					break;
				}
				Require(!AddonVisible("SalvageDialog"), "存在非本流程的分解窗口。");
				target = rewards.Dequeue();
				InventoryItem* slot3 = GetSlot(target);
				Require(slot3 != null && slot3->GetBaseItemId() == target.ItemId && slot3->GetQuantity() == 1, "装备被移动或替换，停止分解。");
				targetCountBefore = Count(Snapshot(), target.ItemId);
				AgentSalvage* ptr4 = AgentSalvage.Instance();
				Require(ptr4 != null, "分解接口不可用。");
				ptr4->SalvageItem(slot3, 0, 0);
				rareConfirmationSent = false;
				confirmReadySince = (desynthReadySince = 0L);
				Move(Step.Confirm);
				break;
			}
			case Step.Confirm:
			{
				AddonSalvageDialog* address2 = (AddonSalvageDialog*)Gui.GetAddonByName("SalvageDialog").Address;
				if (address2 == null || !address2->IsVisible)
				{
					confirmReadySince = (desynthReadySince = 0L);
					break;
				}
				AgentSalvage* ptr6 = AgentSalvage.Instance();
				InventoryItem* slot4 = GetSlot(target);
				Require(slot4 != null && slot4->GetBaseItemId() == target.ItemId && slot4->GetQuantity() == 1, "待分解物品的背包格子发生变化，停止分解。");
				Require(Count(Snapshot(), target.ItemId) == targetCountBefore, "待分解物品数量发生变化，停止分解。");
				Item row = Data.GetExcelSheet<Item>().GetRow(target.ItemId);
				if (address2->ItemIconNode == null || address2->ItemIconNode->IconId != row.Icon || !HasText(&address2->UldManager, row.Name.ToString()))
				{
					if (Elapsed < 2000)
					{
						break;
					}
					throw new InvalidOperationException($"分解窗口校验失败：目标 {target.ItemId} ({row.Name})，图标 {((address2->ItemIconNode != null) ? address2->ItemIconNode->IconId : 0)}/{row.Icon}，Agent ID {((ptr6 != null) ? ptr6->DesynthItemId : 0)}。");
				}
				AtkComponentCheckBox* ptr7 = FindCheckbox(&address2->UldManager, "分解珍稀及独占道具");
				if (confirmReadySince == 0L)
				{
					confirmReadySince = Now;
				}
				if (Now - confirmReadySince < (long)config.DesynthDelaySeconds * 1000L)
				{
					break;
				}
				if (ptr7 != null && !ptr7->IsChecked)
				{
					desynthReadySince = 0L;
					if (!rareConfirmationSent)
					{
						ptr7->SetChecked(isChecked: true);
						Require(Click((AtkComponentButton*)ptr7), "未找到珍稀道具确认框的点击事件。");
						rareConfirmationSent = true;
						Log.Information($"已确认分解本轮珍稀装备 {target.ItemId} ({row.Name})");
					}
					break;
				}
				AtkComponentButton* ptr8 = FindButton(&address2->UldManager, "分解");
				Require(ptr8 != null, "未找到文字为“分解”的按钮，停止以免误点取消。");
				if (ptr8->OwnerNode == null || !ptr8->IsEnabled)
				{
					desynthReadySince = 0L;
					break;
				}
				if (desynthReadySince == 0L)
				{
					desynthReadySince = Now;
					Log.Information($"分解按钮已就绪，再等待 {config.DesynthDelaySeconds} 秒后点击。");
				}
				if (Now - desynthReadySince >= (long)config.DesynthDelaySeconds * 1000L && ClickDesynth(ptr8))
				{
					Move(Step.Salvaged);
				}
				break;
			}
			case Step.Salvaged:
			{
				InventoryItem* slot = GetSlot(target);
				if (slot != null && AdditionalItems.Completed(slot->GetBaseItemId(), target.ItemId, targetCountBefore, Count(Snapshot(), target.ItemId)) && Elapsed > 1500)
				{
					Move(Step.Salvage);
				}
				else if (Elapsed > 10000 && !AddonVisible("SalvageDialog") && !AddonVisible("SalvageResult"))
				{
					throw new InvalidOperationException($"分解未生效：确认窗口已关闭，但物品 {target.ItemId} 数量未减少。分解前={targetCountBefore}，现在={Count(Snapshot(), target.ItemId)}。请查看游戏错误提示，并手动分解该物品验证。");
				}
				break;
			}
			case Step.ScanExtras:
			{
				Require(rowId == 19 && !Conditions[ConditionFlag.BoundByDuty], "额外分解前职业或场景改变。");
				if (AddonVisible("SalvageResult"))
				{
					CloseResult();
					break;
				}
				Require(!AddonVisible("SalvageDialog"), "存在其他分解确认窗口，停止。");
				Slot[] array = AdditionalItems.Select(Snapshot(), config.AdditionalItemIds, (Slot x) => (int)x.Bag, (Slot x) => x.ItemId, (Slot x) => x.Quantity);
				additionalScanDone = true;
				Slot[] array2 = array;
				for (int num2 = 0; num2 < array2.Length; num2++)
				{
					Slot item = array2[num2];
					ValidateAdditionalItem((int)item.ItemId);
					rewards.Enqueue(item);
				}
				Log.Information($"额外分解：检查背包四页，找到 {array.Length} 件列表内物品。");
				Move((array.Length != 0) ? Step.Salvage : Step.Next);
				break;
			}
			case Step.Next:
				if (AddonVisible("SalvageResult"))
				{
					CloseResult();
					break;
				}
				loops++;
				if (config.MaxLoops > 0 && loops >= config.MaxLoops)
				{
					Stop($"已完成 {loops} 轮。");
				}
				else
				{
					Move(Step.WhiteMage);
				}
				break;
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "循环停止");
			Stop(ex.Message);
		}
	}

	private void UpdateJobSwitch(uint expectedJob, int number, Step next)
	{
		if (Conditions[ConditionFlag.BoundByDuty] || Client.TerritoryType == territory || Client.TerritoryType == 0)
		{
			jobReadySince = 0L;
			return;
		}
		if (jobTerritory != Client.TerritoryType)
		{
			jobTerritory = Client.TerritoryType;
			jobReadySince = 0L;
		}
		if (jobReadySince == 0L)
		{
			jobReadySince = Now;
		}
		if (Now - jobReadySince >= 2000)
		{
			if (Objects.LocalPlayer.ClassJob.RowId == expectedJob)
			{
				if (config.AutoRepair)
				{
					Require(next == Step.Queue || next == Step.Open, "维修后的循环目标无效。");
					repairCheckpoint.Begin(expectedJob, Client.TerritoryType,
						next == Step.Queue ? RepairResume.QueueDuty : RepairResume.OpenCoffer);
					Move(Step.Repairing);
				}
				else
				{
					Move(next);
				}
			}
			else if (Now >= nextJobAttempt)
			{
				Require(jobAttempts < 5, $"配装 {number} 切换已尝试 5 次，职业仍为 {Objects.LocalPlayer.ClassJob.RowId}。请查看游戏提示或手动检查配装。");
				int value = SwitchJob(number);
				jobAttempts++;
				nextJobAttempt = Now + 3000;
				jobReadySince = 0L;
				Log.Information($"切换配装 {number}：第 {jobAttempts}/5 次，接口返回 {value}，等待职业 {expectedJob}。");
			}
		}
	}

	private unsafe static int SwitchJob(int number)
	{
		RaptureGearsetModule* ptr = RaptureGearsetModule.Instance();
		Require(ptr != null, "无法读取配装模块。");
		return ptr->EquipGearset(number - 1, 0);
	}

	private unsafe static List<Slot> Snapshot()
	{
		List<Slot> list = new List<Slot>();
		InventoryManager* ptr = InventoryManager.Instance();
		Require(ptr != null, "背包尚未加载。");
		InventoryType[] containers = Containers;
		foreach (InventoryType inventoryType in containers)
		{
			InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(inventoryType);
			Require(inventoryContainer != null && inventoryContainer->IsLoaded && inventoryContainer->Size > 0, "背包或兵装库尚未加载。");
			for (int j = 0; j < inventoryContainer->Size; j++)
			{
				InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(j);
				Require(inventorySlot != null, "无法读取背包格子。");
				list.Add(new Slot(inventoryType, j, inventorySlot->GetBaseItemId(), checked((int)inventorySlot->GetQuantity())));
			}
		}
		return list;
	}

	private static int Count(List<Slot> slots, uint item)
	{
		return (int)slots.Where((Slot x) => x.ItemId == item).Sum((Func<Slot, long>)((Slot x) => x.Quantity));
	}

	private string OpenDiagnostic(List<Slot> slots)
	{
		HashSet<uint> expectedIds = new HashSet<uint>
		{
			(uint)config.CofferId,
			(uint)config.SwordId,
			(uint)config.ShieldId
		};
		IEnumerable<uint> source = (from x in slots
			where x.ItemId != 0 && !expectedIds.Contains(x.ItemId) && Count(slots, x.ItemId) > Count(before, x.ItemId)
			select x.ItemId).Distinct().Take(10);
		return $"箱子({Describe(config.CofferId)}) 开箱前={coffersBefore}；剑({Describe(config.SwordId)})；盾({Describe(config.ShieldId)})；其他新增=[{string.Join(",", source.Select((uint x) => $"{x}:{ItemName((int)x)}"))}]；可操作={Ready()} 职业={Objects.LocalPlayer?.ClassJob.RowId} 战斗={Conditions[ConditionFlag.InCombat]} 施法={Conditions[ConditionFlag.Casting]}";
		string Describe(int id)
		{
			if (id > 0)
			{
				return $"{id} 数量={Count(slots, (uint)id)} 位置=[" + string.Join(",", from x in slots
					where x.ItemId == id
					select $"{x.Bag}:{x.Index}") + "]";
			}
			return "未设置";
		}
	}

	private unsafe static InventoryItem* GetSlot(Slot slot)
	{
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null)
		{
			return null;
		}
		InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(slot.Bag);
		if (inventoryContainer != null)
		{
			return inventoryContainer->GetInventorySlot(slot.Index);
		}
		return null;
	}

	private unsafe static bool Click(AtkComponentButton* button)
	{
		if (button == null || button->OwnerNode == null || !button->IsEnabled)
		{
			return false;
		}
		for (AtkEvent* ptr = button->OwnerNode->AtkResNode.AtkEventManager.Event; ptr != null; ptr = ptr->NextEvent)
		{
			if (ptr->State.EventType == AtkEventType.ButtonClick && ptr->Listener != null)
			{
				AtkEventData atkEventData = default;
				ptr->Listener->ReceiveEvent(AtkEventType.ButtonClick, (int)ptr->Param, ptr, &atkEventData);
				return true;
			}
		}
		return false;
	}

	private unsafe static bool AddonVisible(string name)
	{
		AtkUnitBase* address = (AtkUnitBase*)Gui.GetAddonByName(name).Address;
		if (address != null)
		{
			return address->IsVisible;
		}
		return false;
	}

	private unsafe static AtkComponentButton* FindButton(AtkUldManager* manager, string label, int depth = 0)
	{
		if (manager == null || manager->NodeList == null || depth > 4)
		{
			return null;
		}
		for (int i = 0; i < manager->NodeListCount; i++)
		{
			AtkResNode* ptr = manager->NodeList[i];
			if (ptr == null || !ptr->IsVisible() || (int)ptr->Type < 1000)
			{
				continue;
			}
			AtkComponentBase* component = ((AtkComponentNode*)ptr)->Component;
			if (component == null)
			{
				continue;
			}
			if (component->GetComponentType() == ComponentType.Button)
			{
				AtkComponentButton* ptr2 = (AtkComponentButton*)component;
				if (ptr2->ButtonTextNode != null && ptr2->ButtonTextNode->NodeText.ToString().Trim() == label)
				{
					return ptr2;
				}
			}
			AtkComponentButton* ptr3 = FindButton(&component->UldManager, label, depth + 1);
			if (ptr3 != null)
			{
				return ptr3;
			}
		}
		return null;
	}

	private unsafe static bool ClickDesynth(AtkComponentButton* button)
	{
		if (button == null || button->OwnerNode == null || !button->IsEnabled)
		{
			return false;
		}
		Require(button->ButtonTextNode != null && button->ButtonTextNode->NodeText.ToString().Trim() == "分解", "分解按钮文字发生变化，停止。");
		for (AtkEvent* ptr = button->OwnerNode->AtkResNode.AtkEventManager.Event; ptr != null; ptr = ptr->NextEvent)
		{
			if (ptr->State.EventType == AtkEventType.ButtonClick && ptr->Listener != null)
			{
				Log.Information($"点击分解：文字=分解，节点={button->OwnerNode->NodeId}，事件参数={ptr->Param}，可用={button->IsEnabled}");
				AtkEvent atkEvent = *ptr;
				atkEvent.NextEvent = null;
				atkEvent.State.ReturnFlags = 0;
				atkEvent.State.StateFlags = AtkEventStateFlags.None;
				AtkEventData atkEventData = default;
				ptr->Listener->ReceiveEvent(AtkEventType.ButtonClick, (int)ptr->Param, &atkEvent, &atkEventData);
				return true;
			}
		}
		throw new InvalidOperationException("“分解”按钮没有可用的点击事件。");
	}

	private unsafe static bool HasText(AtkUldManager* manager, string text, int depth = 0)
	{
		if (manager == null || manager->NodeList == null || depth > 4 || string.IsNullOrEmpty(text))
		{
			return false;
		}
		for (int i = 0; i < manager->NodeListCount; i++)
		{
			AtkResNode* ptr = manager->NodeList[i];
			if (ptr == null || !ptr->IsVisible())
			{
				continue;
			}
			if (ptr->Type == NodeType.Text && ((AtkTextNode*)ptr)->NodeText.ToString().Contains(text, StringComparison.Ordinal))
			{
				return true;
			}
			if ((int)ptr->Type >= 1000)
			{
				AtkComponentBase* component = ((AtkComponentNode*)ptr)->Component;
				if (component != null && HasText(&component->UldManager, text, depth + 1))
				{
					return true;
				}
			}
		}
		return false;
	}

	private unsafe static AtkComponentCheckBox* FindCheckbox(AtkUldManager* manager, string label, int depth = 0)
	{
		if (manager == null || manager->NodeList == null || depth > 4)
		{
			return null;
		}
		for (int i = 0; i < manager->NodeListCount; i++)
		{
			AtkResNode* ptr = manager->NodeList[i];
			if (ptr == null || !ptr->IsVisible() || (int)ptr->Type < 1000)
			{
				continue;
			}
			AtkComponentBase* component = ((AtkComponentNode*)ptr)->Component;
			if (component != null)
			{
				if (component->GetComponentType() == ComponentType.CheckBox && HasText(&component->UldManager, label))
				{
					return (AtkComponentCheckBox*)component;
				}
				AtkComponentCheckBox* ptr2 = FindCheckbox(&component->UldManager, label, depth + 1);
				if (ptr2 != null)
				{
					return ptr2;
				}
			}
		}
		return null;
	}

	private unsafe static void CloseResult()
	{
		AtkUnitBase* address = (AtkUnitBase*)Gui.GetAddonByName("SalvageResult").Address;
		if (address != null && address->IsVisible)
		{
			address->Close(fireCallback: true);
		}
	}

	private static string ItemName(int id)
	{
		if (id <= 0)
		{
			return "未设置";
		}
		try
		{
			Item row = Data.GetExcelSheet<Item>().GetRow((uint)id);
			return $"{row.Name} / 品级 {row.LevelItem.RowId}";
		}
		catch
		{
			return "无效 ID";
		}
	}

	private static void ValidateAdditionalItem(int id)
	{
		Require(id > 0, "额外分解 ID 必须大于 0。");
		Item row = Data.GetExcelSheet<Item>().GetRow((uint)id);
		Require(row.Name.ToString().Length > 0 && row.Desynth > 0, $"额外物品 {id} 不存在或不可分解。");
	}

	private void AddAdditionalItem(int id)
	{
		try
		{
			ValidateAdditionalItem(id);
			if (!AdditionalItems.TryAdd(config.AdditionalItemIds, id))
			{
				additionalMessage = "这个 ID 已在列表中。";
				return;
			}
			PI.SavePluginConfig(config);
			additionalMessage = $"已添加 {id}：{ItemName(id)}";
		}
		catch (Exception ex)
		{
			additionalMessage = ex.Message;
		}
	}

	private void Draw()
	{
		if (!visible)
		{
			return;
		}
		ImGui.SetNextWindowSize(new Vector2(650f, 620f), ImGuiCond.FirstUseEver);
		if (ImGui.Begin("拉姆武器箱循环###RamuhLoop", ref visible))
		{
			ImGui.TextWrapped(status);
			if (step == Step.Repairing) ImGui.TextWrapped(repairCheckpoint.Status);
			ImGui.Text("装备最低耐久：" + (minimumEquipmentCondition < 0 ? "数据未就绪" :
				minimumEquipmentCondition == int.MaxValue ? "无可维修装备" :
				$"{Math.Floor(minimumEquipmentCondition / 3d) / 100d:F2}%")
				+ (config.AutoRepair ? "（低于10%自动自行维修）" : "（自动维修已关闭）"));
			if (Running)
			{
				if (ImGui.Button("停止循环"))
				{
					Stop("手动停止；已发送的排本/物品动作不会被撤销。");
				}
			}
			else if (ImGui.Button("开始循环"))
			{
				Start();
			}
			ImGui.BeginDisabled(Running);
			if (ImGui.Checkbox("装备耐久低于10%时自动自行维修", ref config.AutoRepair))
			{
				PI.SavePluginConfig(config);
			}
			ImGui.TextWrapped("切好白魔或骑士配装后，在副本外检查当前装备。低于10%先用暗物质维修，确认耐久恢复后继续排本或开箱；需要对应生产职业等级及足够暗物质。");
			ImGui.InputInt("白魔配装编号 (1–100)", ref config.WhiteMage);
			ImGui.InputInt("骑士配装编号 (1–100)", ref config.Paladin);
			ImGui.InputInt("技能宏编号 (0–99)", ref config.Macro);
			ImGui.Checkbox("公用宏（不勾选为个人宏）", ref config.SharedMacro);
			ImGui.InputInt("进入副本后延时（秒）", ref config.MacroDelay);
			ImGui.InputInt("排本/退本最长等待（秒）", ref config.DutyTimeout);
			ImGui.InputInt("分解操作间隔（秒，1–10）", ref config.DesynthDelaySeconds);
			ImGui.TextWrapped("分解窗口核对后先等待，再勾选珍稀确认；按钮可用后再等待同样时间并点击。默认各等 2 秒。");
			ImGui.InputInt("循环次数（0 为持续循环）", ref config.MaxLoops);
			ImGui.InputInt("武器箱 ID", ref config.CofferId);
			ImGui.TextWrapped(ItemName(config.CofferId));
			ImGui.InputInt("骑士剑 ID", ref config.SwordId);
			ImGui.TextWrapped(ItemName(config.SwordId));
			ImGui.InputInt("骑士盾 ID（无盾填 0）", ref config.ShieldId);
			ImGui.TextWrapped(ItemName(config.ShieldId));
			if (ImGui.Button("保存设置"))
			{
				PI.SavePluginConfig(config);
			}
			ImGui.Separator();
			if (ImGui.Checkbox("剑盾分解后，检查并分解列表内物品", ref config.DesynthAdditionalItems))
			{
				PI.SavePluginConfig(config);
			}
			ImGui.TextWrapped("扫描背包四页，包括原来就有的列表内物品；不扫描兵装库或已穿戴装备。每轮检查一次，同 ID 多件会逐件处理。");
			ImGui.InputInt("添加分解物品 ID", ref newAdditionalId);
			ImGui.SameLine();
			if (ImGui.Button("+"))
			{
				AddAdditionalItem(newAdditionalId);
			}
			ImGui.TextWrapped(additionalMessage);
			ImU8String text = new ImU8String(12, 1);
			text.AppendLiteral("额外分解列表：");
			text.AppendFormatted(config.AdditionalItemIds.Count);
			text.AppendLiteral(" 个 ID");
			ImGui.Text(text);
			if (ImGui.BeginChild("extra-items", new Vector2(0f, 160f)))
			{
				for (int i = 0; i < config.AdditionalItemIds.Count; i++)
				{
					ImGui.PushID(i);
					if (ImGui.SmallButton("删除"))
					{
						config.AdditionalItemIds.RemoveAt(i);
						PI.SavePluginConfig(config);
						ImGui.PopID();
						i--;
					}
					else
					{
						ImGui.SameLine();
						ImU8String text2 = new ImU8String(3, 2);
						text2.AppendFormatted(config.AdditionalItemIds[i]);
						text2.AppendLiteral(" | ");
						text2.AppendFormatted(ItemName(config.AdditionalItemIds[i]));
						ImGui.Text(text2);
						ImGui.PopID();
					}
				}
			}
			ImGui.EndChild();
			ImGui.Separator();
			ImGui.InputText("物品名称搜索", ref search, 100);
			if (ImGui.Button("搜索本机游戏数据") && !string.IsNullOrWhiteSpace(search))
			{
				searchResults = (from x in Data.GetExcelSheet<Item>()
					where x.Name.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
					select x).Take(100).ToList();
			}
			if (ImGui.BeginChild("items", new Vector2(0f, 140f)))
			{
				foreach (Item searchResult in searchResults)
				{
					ImGui.PushID((int)searchResult.RowId);
					ImU8String text3 = new ImU8String(9, 3);
					text3.AppendFormatted(searchResult.RowId);
					text3.AppendLiteral(" | ");
					text3.AppendFormatted(searchResult.Name);
					text3.AppendLiteral(" | 品级 ");
					text3.AppendFormatted(searchResult.LevelItem.RowId);
					ImGui.Text(text3);
					if (ImGui.SmallButton("设为箱"))
					{
						config.CofferId = (int)searchResult.RowId;
					}
					ImGui.SameLine();
					if (ImGui.SmallButton("设为剑"))
					{
						config.SwordId = (int)searchResult.RowId;
					}
					ImGui.SameLine();
					if (ImGui.SmallButton("设为盾"))
					{
						config.ShieldId = (int)searchResult.RowId;
					}
					ImGui.SameLine();
					if (ImGui.SmallButton("+ 加入分解"))
					{
						AddAdditionalItem((int)searchResult.RowId);
					}
					ImGui.PopID();
				}
			}
			ImGui.EndChild();
			ImGui.EndDisabled();
			ImGui.TextWrapped("先开启解除人数限制、关闭等级同步，并配置 DR 退本。箱子需由你或其他插件取得。开箱剑盾分解后按额外列表清理背包。停止不会自动取消已发出的排本申请。");
			ImGui.Separator();
			if (ImGui.Checkbox("如果你知道这是什么，你就可以关了:3", ref config.MouseClickMeow))
			{
				PI.SavePluginConfig(config);
			}
		}
		ImGui.End();
	}

	public void Dispose()
	{
		if (Running)
		{
			Stop("插件卸载，循环停止。");
		}
		Framework.Update -= Update;
		PI.UiBuilder.Draw -= Draw;
		PI.UiBuilder.OpenMainUi -= Show;
		PI.UiBuilder.OpenConfigUi -= Show;
		Commands.RemoveHandler("/ramuhloop");
		catSound.Dispose();
	}
}
