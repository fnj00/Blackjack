using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace PartyBlackjack;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool AutoOpenUiWhenTableOpens { get; set; } = true;

    [NonSerialized]
    private IDalamudPluginInterface? pi;

    public void Initialize(IDalamudPluginInterface pluginInterface) => pi = pluginInterface;

    public void Save() => pi?.SavePluginConfig(this);
}
