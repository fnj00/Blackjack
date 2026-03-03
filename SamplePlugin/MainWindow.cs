using System;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;

namespace PartyBlackjack
{
    public class MainWindow : Window
    {
        private readonly BlackjackTable table;
        private readonly Func<bool> canHost;
        private readonly Action start;
        private readonly Action deal;
        private readonly Action stop;
        private readonly Func<bool> getAutoOpen;
        private readonly Action<bool> setAutoOpen;

        public MainWindow(BlackjackTable table, Func<bool> canHost, Action start, Action deal, Action stop,
            Func<bool> getAutoOpen, Action<bool> setAutoOpen)
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
                MinimumSize = new Vector2(520, 380),
                MaximumSize = new Vector2(1000, 1000)
            };
        }

        public override void Draw()
        {
            // Header
            ImGui.Text(table.TableOpen
                ? (table.RoundInProgress ? "🟢 Table OPEN (Round Active)" : "🟢 Table OPEN")
                : "🔴 Table CLOSED");

            // Config
            bool autoOpen = getAutoOpen();
            if (ImGui.Checkbox("Auto-open UI on table start", ref autoOpen))
                setAutoOpen(autoOpen);

            ImGui.Separator();

            // Host controls
            bool host = canHost();
            if (!host)
                ImGui.TextDisabled("Must be party leader.");

            using var disabledHost = new DisabledScope(!host);
            if (ImGui.Button("Start Table")) start();
            ImGui.SameLine();
            using var disabledTable = new DisabledScope(!table.TableOpen);
            if (ImGui.Button("Deal Round")) 
            {
                deal();
            }
            ImGui.SameLine();
            if (ImGui.Button("Stop Table")) stop();

            ImGui.SameLine(0, 20);
            if (table.TableOpen)
            {
                int expected = table.GetExpectedTotalBet();
                ImGui.Text($"Expected gil: {expected}");
            }

            ImGui.Separator();

            // Clipboard
            if (ImGui.Button("Copy Status")) table.CopyLastPublicMessageToClipboard();
            ImGui.SameLine();
            if (ImGui.Button("Copy /p Status")) table.CopyPublicMessageAsPartyCommandToClipboard();
            ImGui.SameLine();
            ImGui.TextDisabled($"({table.LastPublicMessage.Length}/180)");

            ImGui.Separator();

            // Dealer
            ImGui.Text("Dealer:");
            DrawHandBlock(table.GetDealerCardsDisplay(), table.GetDealerValueDisplay());

            ImGui.Separator();

            // Players
            ImGui.Text("Players:");
            var players = table.GetPlayersSnapshot()
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (players.Length == 0)
            {
                ImGui.TextDisabled("No players.");
                return;
            }

            foreach (var p in players)
            {
                var header = new StringBuilder($"{p.Name} | Next: {p.NextBet}gil");
                if (table.RoundInProgress && p.CurrentBet > 0)
                    header.Append($" | Bet: {p.CurrentBet}gil");
                if (p.PendingPayout > 0)
                    header.Append($" | Send: {p.PendingPayout}gil");
                if (p.SittingOut)
                    header.Append(" | OUT");
                if (p.Stand)
                    header.Append(" | STAND");

                ImGui.Text(header.ToString());

                DrawHandBlock(p.HandCardsDisplay ?? "(no hand)", p.HandValueDisplay ?? "");

                // Action buttons (host for players)
                if (table.RoundInProgress && !p.SittingOut && p.CurrentBet > 0 && !p.Stand)
                {
                    ImGui.Spacing();
                    if (ImGui.Button("Hit##Hit" + p.Name))
                        table.Hit(p.Name);
                    ImGui.SameLine();
                    if (ImGui.Button("Stand##Stand" + p.Name))
                        table.Stand(p.Name);
                    ImGui.SameLine();
                    bool canDouble = p.HandCardCount == 2;
                    using var ddDisabled = new DisabledScope(!canDouble);
                    if (ImGui.Button("Double##Double" + p.Name))
                        table.Double(p.Name);
                    ImGui.SameLine(0.0f, 5.0f);
                    ImGui.TextDisabled(canDouble ? "" : "(initial hand only)");
                }

                // Payout actions
                if (!table.RoundInProgress && p.PendingPayout > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), $"Send {p.PendingPayout}gil:");
                    ImGui.SameLine();
                    if (ImGui.Button($"Trade##T{p.Name}"))
                        ImGuiNET.ImGui.SetClipboardText($"/trade {p.Name}");
                    ImGui.SameLine();
                    if (ImGui.Button($"Sent##S{p.Name}"))
                        table.ClearPayout(p.Name);
                }

                ImGui.Spacing();
            }
        }

        private static void DrawHandBlock(string cardsText, string valueText)
        {
            ImGui.BeginChild($"hand_{cardsText.GetHashCode()}", new Vector2(0, 55), true);
            ImGui.Text(cardsText);
            if (!string.IsNullOrEmpty(valueText))
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), valueText);
            ImGui.EndChild();
        }

        private sealed class DisabledScope : IDisposable
        {
            private readonly bool disable;
            public DisabledScope(bool disable)
            {
                this.disable = disable;
                if (disable) ImGui.BeginDisabled();
            }
            public void Dispose()
            {
                if (disable) ImGui.EndDisabled();
            }
        }
    }
}
