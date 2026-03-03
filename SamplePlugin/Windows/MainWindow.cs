
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
                MinimumSize = new Vector2(520, 400),
                MaximumSize = new Vector2(1000, 1000)
            };
        }

        public override void Draw()
        {
            ImGui.Text(table.TableOpen
                ? (table.RoundInProgress ? "🟢 Table OPEN (Round Active)" : "🟢 Table OPEN")
                : "🔴 Table CLOSED");

            bool autoOpen = getAutoOpen();
            if (ImGui.Checkbox("Auto-open UI on table start", ref autoOpen))
                setAutoOpen(autoOpen);

            ImGui.Separator();

            bool host = canHost();
            if (!host)
                ImGui.TextDisabled("Must be party leader to control table.");

            using var disabledHost = new DisabledScope(!host);
            if (ImGui.Button("Start Table")) start();
            ImGui.SameLine();
            using var disabledTable = new DisabledScope(!table.TableOpen);
            if (ImGui.Button("Deal Round")) deal();
            ImGui.SameLine();
            if (ImGui.Button("Stop Table")) stop();

            if (table.TableOpen)
            {
                int expected = table.GetExpectedTotalBet();
                int received = table.GetTotalReceived();
                ImGui.SameLine(0, 20);
                ImGui.TextColored(received >= expected ? new Vector4(0,1,0,1) : new Vector4(1,0.5f,0,1),
                                  $"Gil: {received}/{expected}");
            }

            ImGui.Separator();

            if (ImGui.Button("Copy Status")) table.CopyLastPublicMessageToClipboard();
            ImGui.SameLine();
            if (ImGui.Button("Copy /p Status")) table.CopyPublicMessageAsPartyCommandToClipboard();
            ImGui.SameLine();
            ImGui.TextDisabled($"({table.LastPublicMessage.Length}/180)");

            ImGui.Separator();

            ImGui.Text("Dealer:");
            DrawHandBlock(table.GetDealerCardsDisplay(), table.GetDealerValueDisplay());

            ImGui.Separator();

            ImGui.Text("Players & Gil Tracking:");

            var snapshots = table.GetPlayersSnapshot()
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (snapshots.Length == 0)
            {
                ImGui.TextDisabled("No players yet.");
                return;
            }

            foreach (var p in snapshots)
            {
                var header = new StringBuilder($"{p.Name}");
                if (!table.RoundInProgress)
                {
                    header.Append($" | Next bet: {p.NextBet}gil | Rec: {p.ReceivedGil}");
                }
                else
                {
                    header.Append($" | Bet: {p.CurrentBet}gil");
                    if (p.ReceivedGil > 0) header.Append($" | Rec: {p.ReceivedGil}");
                }
                if (p.PendingPayout > 0) header.Append($" | Send: {p.PendingPayout}gil");
                if (p.SittingOut) header.Append(" | OUT");
                if (p.Stand) header.Append(" | STAND");
                if (p.DoublePending) header.Append(" | DOUBLE PENDING");

                ImGui.Text(header.ToString());

                DrawHandBlock(p.HandCardsDisplay ?? "(no hand)", p.HandValueDisplay ?? "");

                // Gil confirmation (pre-deal or between rounds)
                if (!table.RoundInProgress && table.TableOpen && p.ReceivedGil < p.NextBet)
                {
                    int missing = p.NextBet - p.ReceivedGil;
                    ImGui.SameLine();
                    if (ImGui.Button($"Mark +{missing}##Rec{p.Name}"))
                        table.MarkGilReceived(p.Name, missing);
                }

                // Double confirmation during round
                if (table.RoundInProgress && p.DoublePending)
                {
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0f, 1f), "DOUBLE PENDING - confirm extra gil:");
                    ImGui.SameLine();
                    if (ImGui.Button($"Confirm Double##ConfDouble{p.Name}"))
                        table.ConfirmDoubleGilReceived(p.Name);
                }

                // Player action buttons (host acts for them)
                if (table.RoundInProgress && !p.SittingOut && p.CurrentBet > 0 && !p.Stand && !p.DoublePending)
                {
                    ImGui.Spacing();
                    if (ImGui.Button($"Hit##{p.Name}")) table.Hit(p.Name);
                    ImGui.SameLine();
                    if (ImGui.Button($"Stand##{p.Name}")) table.Stand(p.Name);
                    ImGui.SameLine();
                    bool canDouble = p.HandCardCount == 2;
                    using var ddDisabled = new DisabledScope(!canDouble);
                    if (ImGui.Button($"Double##{p.Name}")) table.Double(p.Name);
                    ImGui.SameLine(0, 5);
                    ImGui.TextDisabled(canDouble ? "" : "(initial hand only)");
                }

                // Post-round payout
                if (!table.RoundInProgress && p.PendingPayout > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), $"Send {p.PendingPayout}gil to {p.Name}:");
                    ImGui.SameLine();
                    if (ImGui.Button($"Trade##T{p.Name}"))
                        ImGuiNET.ImGui.SetClipboardText($"/trade {p.Name}");
                    ImGui.SameLine();
                    if (ImGui.Button($"Sent##S{p.Name}"))
                        table.ClearPayout(p.Name);  // Add this method if needed: ps.PendingPayout = 0;
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
