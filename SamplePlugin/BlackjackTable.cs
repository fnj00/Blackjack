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

        public BlackjackTable(Func<bool> isHostLeader)
        {
            this.isHostLeader = isHostLeader;
        }

        // -----------------------
        // Table lifecycle
        // -----------------------

        public void OpenTableAndDeal()
        {
            TableOpen = true;
            RoundInProgress = false;

            // fresh deck each time you open table
            deck = new Deck(rng);

            LastPublicMessage = "Table open! Type !bj join. Then !bj bet <amount>. Leader will /bj deal when ready.";
            DealNextRound();
        }

        public void CloseTable()
        {
            TableOpen = false;
            RoundInProgress = false;
            players.Clear();
            stood.Clear();
            dealer = new Hand();
            LastPublicMessage = "Table closed.";
        }

        public void DealNextRound()
        {
            if (!TableOpen) return;

            // Reset round state
            stood.Clear();
            dealer = new Hand();
            RoundInProgress = true;

            // Deal dealer cards
            dealer.Add(deck.Draw());
            dealer.Add(deck.Draw());

            // Deal each player with a bet
            foreach (var ps in players.Values)
            {
                ps.Hand = new Hand();
                ps.HasActedThisRound = false;

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

                ps.SittingOut = false;
                ps.Hand.Add(deck.Draw());
                ps.Hand.Add(deck.Draw());

                // Auto-stand on blackjack
                if (ps.Hand.IsBlackjack)
                    stood.Add(ps.Name);
            }

            UpdatePublicMessage("New round dealt.");
            TryAutoResolveIfDone();
        }

        // -----------------------
        // Party commands (host processes these from party chat)
        // -----------------------

        public bool Join(string player)
        {
            if (!TableOpen) return false;
            if (!isHostLeader()) return false;

            if (players.ContainsKey(player))
                return false;

            players[player] = new PlayerState(player)
            {
                Chips = 1000,
                NextBet = 50
            };

            UpdatePublicMessage($"{player} joined (1000 chips).");
            return true;
        }

        public bool Leave(string player)
        {
            if (!TableOpen) return false;
            if (!isHostLeader()) return false;

            if (!players.Remove(player))
                return false;

            stood.Remove(player);
            UpdatePublicMessage($"{player} left.");
            return true;
        }

        public bool Bet(string player, string? amountToken)
        {
            if (!TableOpen) return false;
            if (!isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps))
                return false;

            if (!int.TryParse(amountToken ?? "", out var amt))
                return false;

            amt = Math.Max(0, amt);
            ps.NextBet = amt;

            UpdatePublicMessage($"{player} set bet to {amt} for next deal.");
            return true;
        }

        public bool Hit(string player)
        {
            if (!TableOpen || !RoundInProgress) return false;
            if (!isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps)) return false;
            if (ps.SittingOut) return false;
            if (ps.Hand == null) return false;

            if (stood.Contains(player)) return false;
            if (ps.Hand.IsBusted) return false;

            ps.Hand.Add(deck.Draw());
            ps.HasActedThisRound = true;

            if (ps.Hand.IsBusted || ps.Hand.BestValue == 21)
                stood.Add(player);

            UpdatePublicMessage($"{player} hits.");
            TryAutoResolveIfDone();
            return true;
        }

        public bool Stand(string player)
        {
            if (!TableOpen || !RoundInProgress) return false;
            if (!isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps)) return false;
            if (ps.SittingOut) return false;
            if (ps.Hand == null) return false;

            if (stood.Contains(player)) return false;

            stood.Add(player);
            ps.HasActedThisRound = true;

            UpdatePublicMessage($"{player} stands.");
            TryAutoResolveIfDone();
            return true;
        }

        public bool Double(string player)
        {
            if (!TableOpen || !RoundInProgress) return false;
            if (!isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps)) return false;
            if (ps.SittingOut) return false;
            if (ps.Hand == null) return false;

            if (stood.Contains(player)) return false;
            if (ps.Hand.IsBusted) return false;
            if (ps.Hand.Cards.Count != 2) return false; // Only on initial hand

            var additionalBet = ps.CurrentBet;
            if (ps.Chips < additionalBet) return false; // Check if can afford without subtracting yet

            ps.CurrentBet += additionalBet;
            ps.Hand.Add(deck.Draw());
            stood.Add(player);
            ps.HasActedThisRound = true;

            UpdatePublicMessage($"{player} doubles down (bet now {ps.CurrentBet}).");
            TryAutoResolveIfDone();
            return true;
        }

        // -----------------------
        // Clipboard helpers (manual paste only)
        // -----------------------

        public void CopyLastPublicMessageToClipboard()
        {
            ImGuiNET.ImGui.SetClipboardText(LastPublicMessage);
        }

        public void CopyPublicMessageAsPartyCommandToClipboard()
        {
            ImGuiNET.ImGui.SetClipboardText($"/p {LastPublicMessage}");
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

                if (!stood.Contains(ps.Name) && !ps.Hand.IsBusted)
                    return false;
            }

            return anyActive;
        }

        private void ResolveDealer()
        {
            while (dealer.BestValue < 17)
                dealer.Add(deck.Draw());
        }

        private void PayoutAndFinish()
        {
            var sb = new StringBuilder();
            sb.Append("RESULTS | ");
            sb.Append($"Dealer {dealer.BestValue}{(dealer.IsBusted ? " BUST" : "")} | ");

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

                sb.Append($"{ps.Name}:{ps.Hand.BestValue}{Tag(ps.Hand)} ");
                sb.Append($"{outcome} ");
                sb.Append($"({(payout >= 0 ? "+" : "")}{payout}c => {ps.Chips}c) | ");
            }

            LastPublicMessage = TrimToChatFriendlyLength(sb.ToString());

            // Round ends but table stays open
            RoundInProgress = false;
            stood.Clear();
        }

        private static string Tag(Hand h)
        {
            if (h.IsBlackjack) return "(BJ)";
            if (h.IsBusted) return "(BUST)";
            return "";
        }

        private static string Compare(Hand player, Hand dealer)
        {
            if (player.IsBusted) return "LOSE";
            if (dealer.IsBusted) return "WIN";
            if (player.IsBlackjack && !dealer.IsBlackjack) return "WIN";
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
                return (bet * 3) / 2; // 3:2, rounded down

            return +bet;
        }

        private void UpdatePublicMessage(string actionLine)
        {
            var sb = new StringBuilder();
            sb.Append(actionLine);
            sb.Append(" | Dealer shows: ");
            sb.Append(dealer.Cards.Count > 0 ? dealer.Cards[0].ToShortString() : "?");
            sb.Append(" | ");

            if (players.Count == 0)
            {
                sb.Append("No players yet. Type !bj join.");
                LastPublicMessage = sb.ToString();
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

                sb.Append($"{ps.Hand.PublicValueString()} ");
                sb.Append($"bet={ps.CurrentBet} ");
                sb.Append($"chips={ps.Chips} ");
                if (stood.Contains(ps.Name)) sb.Append("(stand)");
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
        // Rank: 1(A), 2-10, 11(J), 12(Q), 13(K)
        public int Value => Rank switch
        {
            1 => 11, // Ace initially 11
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
                _ => "♣"
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

            // Fisher-Yates shuffle
            for (int i = cards.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var temp = cards[i];
                cards[i] = cards[j];
                cards[j] = temp;
            }
        }

        public Card Draw()
        {
            if (cards.Count == 0)
                ResetAndShuffle();

            var card = cards[^1]; // Pop from end
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

        public string PublicValueString()
        {
            var sb = new StringBuilder();
            sb.Append(BestValue);
            if (IsBlackjack) sb.Append(" (BJ)");
            if (IsBusted) sb.Append(" (BUST)");
            return sb.ToString();
        }

        public void Add(Card card) => Cards.Add(card);
    }
}
