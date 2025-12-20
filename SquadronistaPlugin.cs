using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Memory;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Squadronista.Solver;
using Squadronista.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

#nullable enable
namespace Squadronista;

public sealed class SquadronistaPlugin : IDalamudPlugin
{
    private readonly WindowSystem _windowSystem = new WindowSystem(nameof(SquadronistaPlugin));
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IClientState _clientState;
    private readonly IPluginLog _pluginLog;
    private readonly IDataManager _dataManager;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly ICommandManager _commandManager;
    private readonly IReadOnlyList<SquadronMission> _allMissions;
    private readonly MainWindow _mainWindow;

    public string Name => "Squadronista";
    public IReadOnlyList<Training> Trainings { get; }
    public List<SquadronMission> AvailableMissions { get; private set; } = new();
    public MissionResults? CurrentSquadronMissionResults { get; private set; }
    private SquadronState? _squadronState;
    private int _lastSelectedMissionRow = -1;

    public SquadronistaPlugin(
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        IPluginLog pluginLog,
        IDataManager dataManager,
        IAddonLifecycle addonLifecycle,
        ICommandManager commandManager,
        IGameGui gameGui)
    {
        if (dataManager == null)
            throw new ArgumentNullException(nameof(dataManager));

        ECommons.ECommonsMain.Init(pluginInterface, this);
        _pluginInterface = pluginInterface;
        _clientState = clientState;
        _pluginLog = pluginLog;
        _dataManager = dataManager;
        _addonLifecycle = addonLifecycle;
        _commandManager = commandManager;
        
        // Load all missions from game data
        _allMissions = dataManager.GetExcelSheet<GcArmyExpedition>()
            ?.Where(x => x.RowId > 0)
            .Select(x => new SquadronMission
            {
                Id = (int)x.RowId,
                Name = x.Name.ToString(),
                Level = x.RequiredLevel,
                IsFlaggedMission = x.RowId switch
                {
                    7 or 14 or 15 or 34 => true,
                    _ => false
                },
                PossibleAttributes = Enumerable.Range(0, x.ExpeditionParams.Count)
                    .Select(i => new Attributes
                    {
                        PhysicalAbility = x.ExpeditionParams[i].RequiredPhysical,
                        MentalAbility = x.ExpeditionParams[i].RequiredMental,
                        TacticalAbility = x.ExpeditionParams[i].RequiredTactical
                    }).ToList().AsReadOnly()
            }).ToList().AsReadOnly() ?? new List<SquadronMission>().AsReadOnly();

        // Load all trainings from game data
        Trainings = dataManager.GetExcelSheet<GcArmyTraining>()
            ?.Where(x => x.RowId > 0 && x.RowId != 7)
            .Select(x => new Training
            {
                RowId = x.RowId,
                Name = x.Name.ToString(),
                PhysicalGained = x.PhysicalBonus,
                MentalGained = x.MentalBonus,
                TacticalGained = x.TacticalBonus
            }).ToList().AsReadOnly() ?? new List<Training>().AsReadOnly();

        _mainWindow = new MainWindow(this, pluginLog, addonLifecycle, gameGui);
        _windowSystem.AddWindow(_mainWindow);

        // Register command
        _commandManager.AddHandler("/squadronista", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Squadronista window"
        });
        _commandManager.AddHandler("/squad", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Squadronista window (short alias)"
        });

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
        _clientState.Logout += ResetCharacterSpecificData;
        
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "GcArmyMemberList", UpdateSquadronState);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "GcArmyExpedition", UpdateExpeditionState);
        _addonLifecycle.RegisterListener(AddonEvent.PostUpdate, "GcArmyExpedition", CheckForMissionChange);
    }

    private void OnCommand(string command, string args)
    {
        ToggleMainWindow();
    }

    private void ToggleMainWindow()
    {
        _mainWindow.IsOpen = !_mainWindow.IsOpen;
    }

    private void ResetCharacterSpecificData(int type, int code)
    {
        _squadronState = null;
        CurrentSquadronMissionResults = null;
        AvailableMissions = new List<SquadronMission>();
    }

    private unsafe void UpdateSquadronState(AddonEvent type, AddonArgs args)
    {
        _pluginLog.Information("Updating squadron state...");
        
        var gcArmyManager = GcArmyManager.Instance();
        if (gcArmyManager == null)
        {
            _pluginLog.Warning("GcArmyManager is null");
            return;
        }

        if (gcArmyManager->Data == null)
        {
            _pluginLog.Warning("GcArmyManager Data is null - squadron data not loaded");
            return;
        }

        // Read squadron members
        var members = new List<SquadronMember>();
        var memberCount = gcArmyManager->GetMemberCount();
        _pluginLog.Debug($"Found {memberCount} squadron members");
        
        for (uint i = 0; i < memberCount && i < 8; i++)
        {
            var member = gcArmyManager->GetMember(i);
            if (member == null)
                continue;

            // Get member name from game data
            var name = $"Member {i + 1}"; // Default fallback
            
            // Try to get the actual name from ENpcResident
            if (member->ENpcResidentId > 0)
            {
                var enpcSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcResident>();
                var enpcData = enpcSheet?.GetRowOrDefault(member->ENpcResidentId);
                var actualName = enpcData?.Singular.ToString();
                if (!string.IsNullOrEmpty(actualName))
                {
                    name = actualName;
                }
            }

            var physical = 0;
            var mental = 0;
            var tactical = 0;

            for(int z = 0; z < memberCount && z < 8; z++)
            {
                var nameIndex = 6 + z * 15;
                var n = (AtkUnitBase*)args.Addon.Address;
                var n2 = n->AtkValues[nameIndex];
                if(n2.Type == ValueType.String)
                {
                    if(n2.GetValueAsString() == name)
                    {
                        physical = n->AtkValues[13 + z * 15].Int;
                        mental = n->AtkValues[14 + z * 15].Int;
                        tactical = n->AtkValues[15 + z * 15].Int;
                        break;
                    }
                }
            }

            _pluginLog.Debug($"""
                {MemoryHelper.ReadRaw((nint)member, 85).ToHexString()}
                Member {i}
                {name}
                Level {member->Level}
                ClassJob {(Job)member->ClassJob}
                Physical {physical}
                Mental {mental}
                Tactical {tactical}
                Experience {member->Experience}
                """);
            
            members.Add(SquadronMember.Create(
                name,
                member->Level,
                member->ClassJob,
                (Solver.Race)member->Race,
                member->Experience
            ).CalculateGrowth(
                _dataManager,
                member->ClassJob,
                member->Experience,
                (byte)physical,
                (byte)mental,
                (byte)tactical
            ));
        }

        // Read bonus attributes
        var bonus = new BonusAttributes
        {
            PhysicalAbility = gcArmyManager->Data->BonusPhysical,
            MentalAbility = gcArmyManager->Data->BonusMental,
            TacticalAbility = gcArmyManager->Data->BonusTactical,
            Cap = 0
        };

        _squadronState = new SquadronState
        {
            Members = members.AsReadOnly(),
            Bonus = bonus,
            CurrentTraining = 0
        };

        RecalculateMissionResults();
    }

    private unsafe void UpdateExpeditionState(AddonEvent type, AddonArgs args)
    {
        _pluginLog.Information("Updating expedition state...");
        
        var addon = (AddonGcArmyExpedition*)args.Addon.Address;
        if (addon == null)
            return;

        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentGcArmyExpedition.Instance();
        if (agent == null)
            return;

        // Update available missions
        // For now, just add all missions that match the player's current level
        // The actual mission list parsing would require more reverse engineering
        AvailableMissions = _allMissions.ToList();

        // Reset last selected mission to force recalculation
        _lastSelectedMissionRow = -1;
        RecalculateMissionResults();
    }

    private unsafe void CheckForMissionChange(AddonEvent type, AddonArgs args)
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentGcArmyExpedition.Instance();
        if (agent == null)
            return;

        // Only recalculate if the selected mission has changed
        if (agent->SelectedRow != _lastSelectedMissionRow)
        {
            _lastSelectedMissionRow = agent->SelectedRow;
            RecalculateMissionResults();
        }
    }

    private unsafe void RecalculateMissionResults()
    {
        if (_squadronState == null || AvailableMissions.Count == 0)
            return;

        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentGcArmyExpedition.Instance();

        var selectedMission = AvailableMissions.First(x => x.Id == Utils.SelectedMission);
        var missionAttributes = selectedMission.PossibleAttributes.First();

        // Check if we're already calculating for this mission
        if (CurrentSquadronMissionResults != null && 
            CurrentSquadronMissionResults.Mission.Id == selectedMission.Id &&
            CurrentSquadronMissionResults.TaskResult != null &&
            !CurrentSquadronMissionResults.TaskResult.IsCompleted)
        {
            // Already calculating for this mission, don't start a new calculation
            return;
        }

        CurrentSquadronMissionResults = new MissionResults
        {
            Mission = selectedMission,
            MissionAttributes = missionAttributes,
            TaskResult = Task.Run(() =>
            {
                var solver = new SquadronSolver(_pluginLog, _squadronState, Trainings);
                return solver.Calculate(selectedMission, missionAttributes);
            })
        };
    }

    public SquadronState? GetSquadronState() => _squadronState;

    public void Dispose()
    {
        _commandManager.RemoveHandler("/squadronista");
        _commandManager.RemoveHandler("/squad");
        _windowSystem.RemoveAllWindows();
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;
        _clientState.Logout -= ResetCharacterSpecificData;
        _addonLifecycle.UnregisterListener(UpdateSquadronState);
        _addonLifecycle.UnregisterListener(UpdateExpeditionState);
        _addonLifecycle.UnregisterListener(CheckForMissionChange);
        ECommonsMain.Dispose();
    }

    public class MissionResults
    {
        public required SquadronMission Mission { get; init; }
        public required Attributes MissionAttributes { get; init; }
        public Task<SquadronSolver.CalculationResults>? TaskResult { get; init; }
    }
}