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

        public int GetExpectedTotalBet() => players.Values.Sum(ps => ps.NextBet);

        public int GetTotalReceived() => players.Values.Sum(ps => ps.ReceivedGil);

        public void MarkGilReceived(string player, int amount)
        {
            if (!players.TryGetValue(player, out var ps)) return;
            ps.ReceivedGil += amount;
            UpdatePublicMessage($"{player} gil received (+{amount}). Total received: {GetTotalReceived()}gil");
            SendUpdate();
        }

        public void ResetReceivedGil()
        {
            foreach (var ps in players.Values)
            {
                ps.ReceivedGil = 0;
                ps.DoublePending = false;
            }
        }

        // -----------------------
        // Table lifecycle
        // -----------------------

        public void OpenTableAndDeal()
        {
            if (!isHostLeader()) return;

            TableOpen = true;
            RoundInProgress = false;
            deck = new Deck(rng);

            LastPublicMessage = "Table open! !bj join, !bj bet <gil>, TRADE gil to me. /bj deal when ready.";
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

            int expected = GetExpectedTotalBet();
            int received = GetTotalReceived();
            if (received < expected)
            {
                UpdatePublicMessage($"Warning: Only {received}/{expected}gil received so far. Proceeding anyway.");
                SendUpdate();
            }

            ResetReceivedGil();

            stood.Clear();
            dealer = new Hand();
            RoundInProgress = true;

            dealer.Add(deck.Draw());
            dealer.Add(deck.Draw());

            foreach (var ps in players.Values.ToList())
            {
                ps.Hand = new Hand();
                ps.CurrentBet = ps.NextBet;
                ps.SittingOut = ps.CurrentBet <= 0;

                if (ps.SittingOut) continue;

                ps.Hand.Add(deck.Draw());
                ps.Hand.Add(deck.Draw());

                if (ps.Hand.IsBlackjack)
                    stood.Add(ps.Name);
            }

            UpdatePublicMessage("New round! Cards dealt. (gil bets collected)");
            TryAutoResolveIfDone();
            SendUpdate();
        }

        // -----------------------
        // Party commands
        // -----------------------

        public bool Join(string player)
        {
            if (!TableOpen || !isHostLeader()) return false;

            if (players.ContainsKey(player)) return false;

            players[player] = new PlayerState(player) { NextBet = 50 };

            UpdatePublicMessage($"{player} joined. !bj bet <gil> & trade me.");
            SendUpdate();
            return true;
        }

        public bool Leave(string player)
        {
            if (!TableOpen || !isHostLeader()) return false;

            if (!players.Remove(player)) return false;

            stood.Remove(player);
            UpdatePublicMessage($"{player} left.");
            SendUpdate();
            return true;
        }

        public bool Bet(string player, string? amountToken)
        {
            if (!TableOpen || !isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps)) return false;

            if (!int.TryParse(amountToken ?? "", out var amt) || amt < 1) return false;

            ps.NextBet = amt;

            UpdatePublicMessage($"{player} bet(next)={amt}gil. Trade me!");
            SendUpdate();
            return true;
        }

        public bool Hit(string player)
        {
            if (!TableOpen || !RoundInProgress || !isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps)) return false;
            if (ps.SittingOut || ps.Hand == null || stood.Contains(player) || ps.Hand.IsBusted) return false;

            var drawnCard = deck.Draw();
            ps.Hand.Add(drawnCard);

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
            if (ps.SittingOut || ps.Hand == null || stood.Contains(player)) return false;

            stood.Add(player);

            UpdatePublicMessage($"{player} stands.");
            TryAutoResolveIfDone();
            SendUpdate();
            return true;
        }

        public bool Double(string player)
        {
            if (!TableOpen || !RoundInProgress || !isHostLeader()) return false;

            if (!players.TryGetValue(player, out var ps)) return false;
            if (ps.SittingOut || ps.Hand == null || stood.Contains(player) || ps.Hand.IsBusted || ps.Hand.Cards.Count != 2) return false;

            int additional = ps.CurrentBet;
            ps.CurrentBet += additional;
            ps.DoublePending = true;

            UpdatePublicMessage($"{player} wants to double! Total bet now {ps.CurrentBet}gil. Host: confirm extra {additional}gil received, then draw.");
            SendUpdate();
            return true;
        }

        public void ConfirmDoubleGilReceived(string player)
        {
            if (!players.TryGetValue(player, out var ps) || !ps.DoublePending) return;

            ps.DoublePending = false;
            ps.ReceivedGil += ps.CurrentBet / 2;  // record the additional gil

            var drawnCard = deck.Draw();
            ps.Hand!.Add(drawnCard);
            stood.Add(player);

            string drawnStr = drawnCard.ToShortString();
            UpdatePublicMessage($"{player} double confirmed (extra gil received), draws {drawnStr}!");
            TryAutoResolveIfDone();
            SendUpdate();
        }

        // -----------------------
        // UI helpers
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
                bool stand = stood.Contains(ps.Name);
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
                    ps.NextBet,
                    ps.CurrentBet,
                    ps.SittingOut,
                    stand,
                    handCount,
                    cards,
                    value,
                    ps.PendingPayout,
                    ps.ReceivedGil,
                    ps.DoublePending
                );
            }).ToArray();
        }

        // -----------------------
        // Round resolution
        // -----------------------

        private void TryAutoResolveIfDone()
        {
            if (!RoundInProgress) return;
            if (!AllActivePlayersDone()) return;

            ResolveDealer();
            PayoutAndFinish();
        }

        private bool AllActivePlayersDone()
        {
            bool anyActive = false;

            foreach (var ps in players.Values)
            {
                if (ps.SittingOut || ps.CurrentBet <= 0 || ps.Hand == null || ps.DoublePending) continue;

                anyActive = true;
                if (!stood.Contains(ps.Name)) return false;
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
                draws += (draws.Length > 0 ? " " : "") + c.ToShortString();
            }
            return draws;
        }

        private void PayoutAndFinish()
        {
            var sb = new StringBuilder();
            sb.Append("RESULTS | ");

            string dealerDraws = ResolveDealer();
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
                    sb.Append($"{ps.Name}: OUT | ");
                    continue;
                }

                string outcome = Compare(ps.Hand, dealer);
                int sendback = ComputeSendback(ps.CurrentBet, ps.Hand, dealer, outcome);
                ps.PendingPayout = sendback;

                string payoutStr = sendback == 0 ? "LOSE (kept bet)" : $"{outcome} (send {sendback}gil)";

                sb.Append($"{ps.Name}: ");
                sb.Append(string.Join(" ", ps.Hand.Cards.Select(c => c.ToShortString())));
                sb.Append(" ");
                sb.Append(ps.Hand.ValueDisplay());
                sb.Append(" ");
                sb.Append(payoutStr);
                sb.Append(" | ");
            }

            LastPublicMessage = TrimToChatFriendlyLength(sb.ToString());
            RoundInProgress = false;
            stood.Clear();
            foreach (var ps in players.Values)
            {
                ps.CurrentBet = 0;
                ps.Hand = null;
            }
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

        private static int ComputeSendback(int bet, Hand player, Hand dealer, string outcome)
        {
            if (outcome == "LOSE") return 0;
            if (outcome == "PUSH") return bet;
            if (outcome == "BJ WIN") return (bet * 5) / 2; // 2.5×
            return bet * 2;
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
                sb.Append("No players. !bj join");
                LastPublicMessage = TrimToChatFriendlyLength(sb.ToString());
                return;
            }

            foreach (var ps in players.Values.OrderBy(p => p.Name))
            {
                sb.Append(ps.Name);
                sb.Append(": ");

                if (!RoundInProgress)
                {
                    sb.Append($"bet(next)={ps.NextBet}gil");
                    if (ps.ReceivedGil > 0) sb.Append($" rec={ps.ReceivedGil}");
                    if (ps.PendingPayout > 0) sb.Append($" send={ps.PendingPayout}");
                    sb.Append(" | ");
                    continue;
                }

                if (ps.SittingOut || ps.CurrentBet <= 0 || ps.Hand == null)
                {
                    sb.Append("OUT | ");
                    continue;
                }

                sb.Append(ps.Hand.FullDisplay());
                sb.Append($" bet={ps.CurrentBet}gil");
                if (ps.DoublePending) sb.Append(" (DOUBLE PENDING)");
                else if (stood.Contains(ps.Name)) sb.Append(" (STAND)");
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
    // Snapshots & Internal classes
    // -----------------------

    internal readonly record struct PlayerSnapshot(
        string Name,
        int NextBet,
        int CurrentBet,
        bool SittingOut,
        bool Stand,
        int HandCardCount,
        string? HandCardsDisplay,
        string? HandValueDisplay,
        int PendingPayout,
        int ReceivedGil,
        bool DoublePending
    );

    internal sealed class PlayerState
    {
        public string Name { get; }
        public int NextBet { get; set; } = 50;
        public int CurrentBet { get; set; }
        public bool SittingOut { get; set; }
        public Hand? Hand { get; set; }
        public int PendingPayout { get; set; }
        public int ReceivedGil { get; set; }
        public bool DoublePending { get; set; }

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
