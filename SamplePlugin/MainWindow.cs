using System;
using System.Linq;
using System.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace PartyBlackjack;

internal sealed class MainWindow : Window
{
    private readonly BlackjackTable table;

    private readonly Func<bool> canHost;
    private readonly Action start;
    private readonly Action deal;
    private readonly Action stop;

    private readonly Func<bool> getAutoOpen;
    private readonly Action<bool> setAutoOpen;

    public MainWindow(
        BlackjackTable table,
        Func<bool> canHost,
        Action start,
        Action deal,
        Action stop,
        Func<bool> getAutoOpen,
        Action<bool> setAutoOpen)
        : base("Party Blackjack###PartyBlackjackMain")
    {
        this.table = table;
        this.canHost = canHost;
        this.start = start;
        this.deal = deal;
        this.stop = stop;
        this.getAutoOpen = getAutoOpen;
        this.setAutoOpen = setAutoOpen;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(460, 300),
            MaximumSize = new System.Numerics.Vector2(900, 900),
        };
    }

    public override void Draw()
    {
        // Header state
        ImGui.TextUnformatted(table.TableOpen
            ? (table.RoundInProgress ? "Table: OPEN (Round in progress)" : "Table: OPEN (Between rounds)")
            : "Table: CLOSED");

        // Auto-open checkbox
        bool autoOpen = getAutoOpen();
        if (ImGui.Checkbox("Auto-open UI when table opens", ref autoOpen))
            setAutoOpen(autoOpen);

        ImGui.Separator();

        // Host controls row
        bool host = canHost();

        if (!host)
            ImGui.TextDisabled("Host controls disabled (you must be party leader).");

        using (new DisabledScope(!host))
        {
            if (ImGui.Button("Start"))
                start();

            ImGui.SameLine();

            // Deal only makes sense when table is open
            using (new DisabledScope(!table.TableOpen))
            {
                if (ImGui.Button("Deal"))
                    deal();
            }

            ImGui.SameLine();

            using (new DisabledScope(!table.TableOpen))
            {
                if (ImGui.Button("Stop"))
                    stop();
            }
        }

        ImGui.Separator();

        // Safe clipboard actions (manual paste)
        if (ImGui.Button("Copy Status"))
            table.CopyLastPublicMessageToClipboard();

        ImGui.SameLine();
        if (ImGui.Button("Copy /p Status"))
            table.CopyPublicMessageAsPartyCommandToClipboard();

        ImGui.SameLine();
        ImGui.TextDisabled($"({table.LastPublicMessage.Length} chars)");

        ImGui.Separator();

        // Dealer section
        ImGui.TextUnformatted("Dealer");
        DrawHandBlock(
            cardsText: table.GetDealerCardsDisplay(),
            valueText: table.GetDealerValueDisplay()
        );

        ImGui.Separator();

        // Players section
        ImGui.TextUnformatted("Players");
        var players = table.GetPlayersSnapshot().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        if (players.Length == 0)
        {
            ImGui.TextDisabled("No players have joined yet.");
            return;
        }

        foreach (var p in players)
        {
            var header = new StringBuilder();
            header.Append(p.Name);
            header.Append("  ");
            header.Append($"{p.Chips}c");
            header.Append("  ");
            header.Append($"bet(next)={p.NextBet}");
            if (table.RoundInProgress)
                header.Append($"  bet(this)={p.CurrentBet}");

            if (p.SittingOut) header.Append("  [OUT]");
            if (p.Stand) header.Append("  [STAND]");

            ImGui.TextUnformatted(header.ToString());

            DrawHandBlock(
                cardsText: p.HandCardsDisplay ?? "(no hand)",
                valueText: p.HandValueDisplay ?? ""
            );

            ImGui.Spacing();
        }
    }

    private static void DrawHandBlock(string cardsText, string valueText)
    {
        ImGui.BeginChild($"##hand_{cardsText.GetHashCode()}", new System.Numerics.Vector2(0, 52), true);
        ImGui.TextUnformatted(cardsText);
        if (!string.IsNullOrWhiteSpace(valueText))
            ImGui.TextUnformatted(valueText);
        ImGui.EndChild();
    }

    private sealed class DisabledScope : IDisposable
    {
        private readonly bool disabled;
        public DisabledScope(bool disabled)
        {
            this.disabled = disabled;
            if (disabled) ImGui.BeginDisabled();
        }
        public void Dispose()
        {
            if (disabled) ImGui.EndDisabled();
        }
    }
}
