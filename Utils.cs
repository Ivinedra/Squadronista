using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Squadronista;
public static class Utils
{
    public static unsafe int SelectedMission => AgentGcArmyExpedition.Instance()->IsAgentActive()?*(int*)(*(nint*)((nint)(AgentGcArmyExpedition.Instance()) + 40) + 6536):0;
}