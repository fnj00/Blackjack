using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ImGuiNET;

namespace PartyBlackjack
{
    public class BlackjackTable
    {
        private readonly Func<bool> isHostLeader;
        private readonly Action<string> onSendPartyMsg;
        private readonly Random rng = new Random();

        private Deck deck = new Deck();
        private Hand dealer = new Hand();

        // Table state persists across rounds
        private readonly Dictionary<string, PlayerState> players = new(StringComparer.OrdinalIgnoreCase);

        // Round-only state
        private readonly HashSet<string> stood = new(StringComparer.OrdinalIgnoreCase);

        public bool TableOpen { get; private set; }
        public bool RoundInProgress { get; private set; }

        public string LastPublicMessage { get; private set; } = "No table.";

        public BlackjackTable(Func<bool> isHostLeader, Action<string> onSendPartyMsg)
        {
            this.isHostLeader = isHostLeader;
            this.onSendPartyMsg = onSendPartyMsg;
        }

        public void SendUpdate() => onSendPartyMsg(LastPublicMessage);

        public void CopyLastPublicMessageToClipboard() => ImGuiNET.ImGui.SetClipboardText(LastPublicMessage);

        public void CopyPublicMessageAsPartyCommandToClipboard() => ImGuiNET.ImGui.SetClipboardText($"/p {LastPublicMessage}");

        // -----------------------
        // Table lifecycle
        // -----------------------

        public void OpenTableAndDeal()
        {
            if (!isHostLeader()) return;

            TableOpen = true;
            RoundInProgress = false;
            deck = new Deck(rng);

            LastPublicMessage = "Table open! Type !bj join. Then !bj bet <amount>. Leader will /bj deal when ready.";
            SendUpdate();

            DealNextRound();
        }

        public void CloseTable()
        {
            if (!isHostLeader()) return;

            LastPublicMessage = "Table closed.";
            SendUpdate();

            TableOpen = false;
            RoundInProgress = false;
            players.Clear();
            stood.Clear();
            dealer = new Hand();
        }

        public void DealNextRound()
        {
            if (!TableOpen || !isHostLeader()) return;

            // Reset round state
            stood.Clear();
            dealer = new Hand();
            RoundInProgress = true;

            // Deal dealer cards
            dealer.Add(deck.Draw());
            dealer.Add(deck.Draw());

            // Deal each player with a bet
            foreach (var ps in players.Values.ToList())
            {
                ps.Hand = new Hand();
                ps.HasActedThisRound = false;
                ps.SittingOut = false;
                ps.CurrentBet = 0;

                // Default bet if none set
                if (ps.NextBet <= 0) ps.NextBet = 50;

                // Clamp bet to available chips (or 0 -> sit out)
                var bet = Math.Clamp(ps.NextBet, 0, ps.Chips);
                ps.CurrentBet = bet;

                if (bet == 0)
                {
                    ps.SittingOut = true;
                    continue;
                }

                ps.Hand.Add(deck.Draw());
                ps.Hand.Add(deck.Draw());

                // Auto-stand on blackjack
                if (ps.Hand.IsBlackjack)
                    stood.Add(ps.Name);
            }

            UpdatePublicMessage("New round! Cards dealt.");
            TryAutoResolveIfDone();
            SendUpdate();
        }

        // -----------------------
        // Party commands (host processes these from party chat)
        // -----------------------

        public bool Join(string player)
        {
            if (!TableOpen || !isHostLeader()) return false;

            if (players.ContainsKey(player))
                return false;

            players[player] = new PlayerState(player)
            {
                Chips = 1000,
                NextBet = 50
            };

            UpdatePublicMessage($"{player} joined (1000 chips).");
            SendUpdate();
            return true;
        }

        public bool Leave(string player)
        {
            if (!TableOpen || !isHostLeader()) return false;

            if (!players.Remove(player))
                return false;

            stood.Remove(player);
            UpdatePublicMessage($"{player} left.");
            SendUpdate();
            return true;
        }

        public bool Bet(string player, string? amountToken)
        {
            if (!TableOpen || !isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps))
                return false;

            if (!int.TryParse(amountToken ?? "", out var amt))
                return false;

            amt = Math.Max(0, amt);
            ps.NextBet = amt;

            UpdatePublicMessage($"{player} set bet to {amt} for next deal.");
            SendUpdate();
            return true;
        }

        public bool Hit(string player)
        {
            if (!TableOpen || !RoundInProgress || !isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps)) return false;
            if (ps.SittingOut) return false;
            if (ps.Hand == null) return false;
            if (stood.Contains(player)) return false;
            if (ps.Hand.IsBusted) return false;

            var drawnCard = deck.Draw();
            ps.Hand.Add(drawnCard);
            ps.HasActedThisRound = true;

            string drawnStr = drawnCard.ToShortString();
            if (ps.Hand.IsBusted || ps.Hand.BestValue == 21)
                stood.Add(player);

            UpdatePublicMessage($"{player} hits, draws {drawnStr}!");
            TryAutoResolveIfDone();
            SendUpdate();
            return true;
        }

        public bool Stand(string player)
        {
            if (!TableOpen || !RoundInProgress || !isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps)) return false;
            if (ps.SittingOut) return false;
            if (ps.Hand == null) return false;
            if (stood.Contains(player)) return false;

            stood.Add(player);
            ps.HasActedThisRound = true;

            UpdatePublicMessage($"{player} stands.");
            TryAutoResolveIfDone();
            SendUpdate();
            return true;
        }

        public bool Double(string player)
        {
            if (!TableOpen || !RoundInProgress || !isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps)) return false;
            if (ps.SittingOut) return false;
            if (ps.Hand == null) return false;
            if (stood.Contains(player)) return false;
            if (ps.Hand.IsBusted) return false;
            if (ps.Hand.Cards.Count != 2) return false;

            int origBet = ps.CurrentBet;
            int totalBetNeeded = origBet * 2;
            if (ps.Chips < totalBetNeeded) return false;

            ps.CurrentBet = totalBetNeeded;
            var drawnCard = deck.Draw();
            ps.Hand.Add(drawnCard);
            stood.Add(player);
            ps.HasActedThisRound = true;

            string drawnStr = drawnCard.ToShortString();
            UpdatePublicMessage($"{player} doubles down (bet now {ps.CurrentBet}), draws {drawnStr}!");
            TryAutoResolveIfDone();
            SendUpdate();
            return true;
        }

        // -----------------------
        // UI helpers (full card faces locally)
        // -----------------------

        public string GetDealerCardsDisplay()
        {
            if (!TableOpen) return "(no table)";
            if (dealer.Cards.Count == 0) return "(no cards)";
            return string.Join("  ", dealer.Cards.Select(c => c.ToShortString()));
        }

        public string GetDealerValueDisplay()
        {
            if (!TableOpen) return "";
            if (dealer.Cards.Count == 0) return "";
            return $"Value: {dealer.BestValue}{(dealer.IsBusted ? " (BUST)" : "")}";
        }

        public PlayerSnapshot[] GetPlayersSnapshot()
        {
            return players.Values.Select(ps =>
            {
                var stand = stood.Contains(ps.Name);
                int handCount = ps.Hand?.Cards.Count ?? 0;

                string? cards = null;
                string? value = null;

                if (ps.Hand != null && ps.Hand.Cards.Count > 0)
                {
                    cards = string.Join("  ", ps.Hand.Cards.Select(c => c.ToShortString()));
                    value = $"Value: {ps.Hand.BestValue}"
                            + (ps.Hand.IsBlackjack ? " (BJ)" : "")
                            + (ps.Hand.IsBusted ? " (BUST)" : "");
                }

                return new PlayerSnapshot(
                    ps.Name,
                    ps.Chips,
                    ps.NextBet,
                    ps.CurrentBet,
                    ps.SittingOut,
                    stand,
                    handCount,
                    cards,
                    value
                );
            }).ToArray();
        }

        // -----------------------
        // Round resolution
        // -----------------------

        private void TryAutoResolveIfDone()
        {
            if (!RoundInProgress) return;

            if (!AllActivePlayersDone())
                return;

            ResolveDealer();
            PayoutAndFinish();
        }

        private bool AllActivePlayersDone()
        {
            var anyActive = false;

            foreach (var ps in players.Values)
            {
                if (ps.SittingOut || ps.CurrentBet <= 0 || ps.Hand == null)
                    continue;

                anyActive = true;

                if (!stood.Contains(ps.Name))
                    return false;
            }

            return anyActive;
        }

        private string ResolveDealer()
        {
            string draws = "";
            while (dealer.BestValue < 17)
            {
                var c = deck.Draw();
                dealer.Add(c);
                if (!string.IsNullOrEmpty(draws)) draws += " ";
                draws += c.ToShortString();
            }
            return draws;
        }

        private void PayoutAndFinish()
        {
            var sb = new StringBuilder();
            sb.Append("RESULTS | ");

            var dealerDraws = ResolveDealer();
            sb.Append("Dealer: ");
            sb.Append(string.Join(" ", dealer.Cards.Select(c => c.ToShortString())));
            sb.Append(" ");
            sb.Append(dealer.ValueDisplay());
            if (!string.IsNullOrEmpty(dealerDraws)) sb.Append($" (hit {dealerDraws})");
            sb.Append(" | ");

            foreach (var ps in players.Values.OrderBy(p => p.Name))
            {
                if (ps.SittingOut || ps.CurrentBet <= 0 || ps.Hand == null)
                {
                    sb.Append($"{ps.Name}: OUT ({ps.Chips}c) | ");
                    continue;
                }

                var outcome = Compare(ps.Hand, dealer);
                var payout = ComputePayout(ps.CurrentBet, ps.Hand, dealer, outcome);

                ps.Chips += payout;

                sb.Append($"{ps.Name}: ");
                sb.Append(string.Join(" ", ps.Hand.Cards.Select(c => c.ToShortString())));
                sb.Append(" ");
                sb.Append(ps.Hand.ValueDisplay());
                sb.Append(" ");
                sb.Append(outcome);
                sb.Append($" ({(payout >= 0 ? "+" : "")}{payout}c => {ps.Chips}c) | ");
            }

            LastPublicMessage = TrimToChatFriendlyLength(sb.ToString());

            // Round ends but table stays open
            RoundInProgress = false;
            stood.Clear();
            SendUpdate();
        }

        private static string Compare(Hand player, Hand dealer)
        {
            if (player.IsBusted) return "LOSE";
            if (dealer.IsBusted) return "WIN";
            if (player.IsBlackjack && !dealer.IsBlackjack) return "BJ WIN";
            if (!player.IsBlackjack && dealer.IsBlackjack) return "LOSE";
            if (player.BestValue > dealer.BestValue) return "WIN";
            if (player.BestValue < dealer.BestValue) return "LOSE";
            return "PUSH";
        }

        private static int ComputePayout(int bet, Hand player, Hand dealer, string outcome)
        {
            if (outcome == "PUSH") return 0;
            if (outcome == "LOSE") return -bet;

            // WIN
            if (player.IsBlackjack && !dealer.IsBlackjack)
                return (bet * 3) / 2; // 3:2

            return +bet;
        }

        private void UpdatePublicMessage(string actionLine)
        {
            var sb = new StringBuilder();
            sb.Append(actionLine);
            sb.Append(" | Dealer up: ");
            sb.Append(dealer.Cards.Count > 0 ? dealer.Cards[0].ToShortString() : "?");
            sb.Append(" | ");

            if (players.Count == 0)
            {
                sb.Append("No players yet. Type !bj join.");
                LastPublicMessage = TrimToChatFriendlyLength(sb.ToString());
                return;
            }

            foreach (var ps in players.Values.OrderBy(p => p.Name))
            {
                sb.Append(ps.Name);
                sb.Append(": ");

                if (!RoundInProgress)
                {
                    sb.Append($"{ps.Chips}c bet(next)={ps.NextBet}");
                    sb.Append(" | ");
                    continue;
                }

                if (ps.SittingOut || ps.CurrentBet <= 0 || ps.Hand == null)
                {
                    sb.Append($"OUT ({ps.Chips}c) ");
                    sb.Append(" | ");
                    continue;
                }

                sb.Append(ps.Hand.FullDisplay());
                sb.Append($" bet={ps.CurrentBet}");
                if (stood.Contains(ps.Name)) sb.Append(" (STAND)");
                sb.Append(" | ");
            }

            LastPublicMessage = TrimToChatFriendlyLength(sb.ToString());
        }

        private static string TrimToChatFriendlyLength(string s)
        {
            const int max = 180;
            if (s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }
    }

    // -----------------------
    // Snapshots for UI
    // -----------------------

    internal readonly record struct PlayerSnapshot(
        string Name,
        int Chips,
        int NextBet,
        int CurrentBet,
        bool SittingOut,
        bool Stand,
        int HandCardCount,
        string? HandCardsDisplay,
        string? HandValueDisplay
    );

    // -----------------------
    // Internal state classes
    // -----------------------

    internal sealed class PlayerState
    {
        public string Name { get; }
        public int Chips { get; set; }
        public int NextBet { get; set; }
        public int CurrentBet { get; set; }
        public bool SittingOut { get; set; }
        public bool HasActedThisRound { get; set; }
        public Hand? Hand { get; set; }

        public PlayerState(string name) => Name = name;
    }

    internal readonly record struct Card(int Rank, int Suit)
    {
        public int Value => Rank switch
        {
            1 => 11,
            >= 11 => 10,
            _ => Rank
        };

        public string ToShortString()
        {
            string r = Rank switch
            {
                1 => "A",
                11 => "J",
                12 => "Q",
                13 => "K",
                _ => Rank.ToString()
            };

            string s = Suit switch
            {
                0 => "♠",
                1 => "♥",
                2 => "♦",
                3 => "♣"
            };

            return r + s;
        }
    }

    internal sealed class Deck
    {
        private readonly Random rng;
        private readonly List<Card> cards = new(52);

        public Deck(Random rng)
        {
            this.rng = rng;
            ResetAndShuffle();
        }

        private void ResetAndShuffle()
        {
            cards.Clear();
            for (int suit = 0; suit < 4; suit++)
                for (int rank = 1; rank <= 13; rank++)
                    cards.Add(new Card(rank, suit));

            for (int i = cards.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (cards[i], cards[j]) = (cards[j], cards[i]);
            }
        }

        public Card Draw()
        {
            if (cards.Count == 0)
                ResetAndShuffle();

            var card = cards[^1];
            cards.RemoveAt(cards.Count - 1);
            return card;
        }
    }

    internal sealed class Hand
    {
        public List<Card> Cards { get; } = new();

        public bool IsBlackjack => Cards.Count == 2 && BestValue == 21;
        public bool IsBusted => BestValue > 21;

        public int BestValue
        {
            get
            {
                int value = Cards.Sum(c => c.Value);
                int aces = Cards.Count(c => c.Rank == 1);
                while (value > 21 && aces > 0)
                {
                    value -= 10;
                    aces--;
                }
                return value;
            }
        }

        public string ValueDisplay() => $"{BestValue}{Hand.Tag(this)}";

        public string FullDisplay() => string.Join(" ", Cards.Select(c => c.ToShortString())) + " " + ValueDisplay();

        public void Add(Card card) => Cards.Add(card);

        private static string Tag(Hand h) => h.IsBlackjack ? " (BJ)" : h.IsBusted ? " (BUST)" : "";
    }
}
