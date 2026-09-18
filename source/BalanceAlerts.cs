using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    internal sealed class BalanceAlertUpdate
    {
        public bool IsCny;
        public decimal TodaySpend;
        public string Notification;
        public bool Critical;
    }

    internal sealed class BalanceAlertState
    {
        public string Date = String.Empty;
        public string Currency = String.Empty;
        public decimal LastBalance;
        public decimal TodaySpend;
        public bool DailySpendAlerted;
        public bool Low10Armed = true;
        public bool Low5Armed = true;
        public bool Low1Armed = true;
    }

    internal sealed class DeepSeekBalanceAlertTracker
    {
        private const decimal DailySpendThreshold = 15.0m;
        private const decimal Low10Threshold = 10.0m;
        private const decimal Low5Threshold = 5.0m;
        private const decimal Low1Threshold = 1.0m;

        private readonly string _statePath;
        private readonly string _pluginUsagePath;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };
        private BalanceAlertState _state;

        public DeepSeekBalanceAlertTracker(string statePath, string pluginUsagePath)
        {
            _statePath = statePath;
            _pluginUsagePath = pluginUsagePath;
            _state = LoadState();
        }

        public BalanceAlertUpdate Observe(BalanceResult result)
        {
            BalanceAlertUpdate update = new BalanceAlertUpdate();
            if (result == null || !result.Ok || result.Amount < 0)
            {
                return update;
            }

            string currency = String.IsNullOrEmpty(result.Currency)
                ? "CNY"
                : result.Currency.ToUpperInvariant();
            if (currency != "CNY")
            {
                return update;
            }

            update.IsCny = true;
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (!String.Equals(_state.Date, today, StringComparison.Ordinal)
                || !String.Equals(_state.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                _state.Date = today;
                _state.Currency = currency;
                _state.LastBalance = result.Amount;
                _state.TodaySpend = 0.0m;
                _state.DailySpendAlerted = false;
                _state.Low10Armed = true;
                _state.Low5Armed = true;
                _state.Low1Armed = true;
            }

            if (_state.LastBalance > 0.0m && result.Amount < _state.LastBalance)
            {
                _state.TodaySpend += _state.LastBalance - result.Amount;
            }

            decimal pluginUsage = ReadPluginUsage(today, currency);
            if (pluginUsage > _state.TodaySpend)
            {
                _state.TodaySpend = pluginUsage;
            }

            _state.LastBalance = result.Amount;
            _state.Date = today;
            _state.Currency = currency;
            update.TodaySpend = _state.TodaySpend;

            List<string> alerts = new List<string>();
            if (!_state.DailySpendAlerted && _state.TodaySpend >= DailySpendThreshold)
            {
                _state.DailySpendAlerted = true;
                update.Critical = true;
                alerts.Add(
                    "今日已花费 "
                    + FormatMoney(_state.TodaySpend)
                    + "，已超过 "
                    + FormatMoney(DailySpendThreshold));
            }

            string lowBalanceAlert = CheckLowBalance(result.Amount);
            if (!String.IsNullOrEmpty(lowBalanceAlert))
            {
                alerts.Add(lowBalanceAlert);
                if (result.Amount <= Low1Threshold)
                {
                    update.Critical = true;
                }
            }

            if (alerts.Count > 0)
            {
                update.Notification = "余额告警：" + String.Join("；", alerts.ToArray());
            }

            SaveState();
            return update;
        }

        public void Reset()
        {
            _state = new BalanceAlertState();
            try
            {
                if (File.Exists(_statePath))
                {
                    File.Delete(_statePath);
                }
            }
            catch
            {
            }
        }

        private string CheckLowBalance(decimal amount)
        {
            if (amount > Low10Threshold)
            {
                _state.Low10Armed = true;
                _state.Low5Armed = true;
                _state.Low1Armed = true;
                return null;
            }

            if (amount > Low5Threshold)
            {
                _state.Low5Armed = true;
                _state.Low1Armed = true;
                if (_state.Low10Armed)
                {
                    _state.Low10Armed = false;
                    return "余额仅剩 " + FormatMoney(amount) + "（已达到 10 元提醒线）";
                }

                return null;
            }

            if (amount > Low1Threshold)
            {
                _state.Low1Armed = true;
                if (_state.Low5Armed || _state.Low10Armed)
                {
                    _state.Low5Armed = false;
                    _state.Low10Armed = false;
                    return "余额仅剩 " + FormatMoney(amount) + "（已达到 5 元提醒线）";
                }

                return null;
            }

            if (_state.Low1Armed || _state.Low5Armed || _state.Low10Armed)
            {
                _state.Low1Armed = false;
                _state.Low5Armed = false;
                _state.Low10Armed = false;
                return "余额仅剩 " + FormatMoney(amount) + "（已达到 1 元提醒线）";
            }

            return null;
        }

        private decimal ReadPluginUsage(string today, string currency)
        {
            try
            {
                if (!File.Exists(_pluginUsagePath))
                {
                    return 0.0m;
                }

                string text = File.ReadAllText(_pluginUsagePath, Encoding.UTF8);
                Match dateMatch = Regex.Match(
                    text,
                    @"""date""[ \t]*:[ \t]*""(?<value>(?:\\.|[^""])*)""");
                Match amountMatch = Regex.Match(
                    text,
                    @"""todayUsage""[ \t]*:[ \t]*""?(?<value>-?[0-9]+(?:\.[0-9]+)?)""?");
                Match currencyMatch = Regex.Match(
                    text,
                    @"""lastCurrency""[ \t]*:[ \t]*""(?<value>(?:\\.|[^""])*)""");
                if (!dateMatch.Success || !amountMatch.Success || !currencyMatch.Success)
                {
                    return 0.0m;
                }

                string ledgerDate = dateMatch.Groups["value"].Value;
                string ledgerCurrency = currencyMatch.Groups["value"].Value;
                if (!String.Equals(ledgerDate, today, StringComparison.Ordinal)
                    || !String.Equals(ledgerCurrency, currency, StringComparison.OrdinalIgnoreCase))
                {
                    return 0.0m;
                }

                decimal amount;
                if (Decimal.TryParse(
                    amountMatch.Groups["value"].Value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out amount))
                {
                    return amount < 0.0m ? 0.0m : amount;
                }
            }
            catch
            {
            }

            return 0.0m;
        }

        private BalanceAlertState LoadState()
        {
            try
            {
                if (!File.Exists(_statePath))
                {
                    return new BalanceAlertState();
                }

                BalanceAlertState state =
                    JsonSerializer.Deserialize<BalanceAlertState>(
                        File.ReadAllText(_statePath, Encoding.UTF8),
                        JsonOptions);
                return state ?? new BalanceAlertState();
            }
            catch
            {
                return new BalanceAlertState();
            }
        }

        private void SaveState()
        {
            try
            {
                string directory = Path.GetDirectoryName(_statePath);
                if (!String.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temporaryPath = _statePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(_state, JsonOptions),
                    new UTF8Encoding(false));

                if (File.Exists(_statePath))
                {
                    File.Replace(temporaryPath, _statePath, null);
                }
                else
                {
                    File.Move(temporaryPath, _statePath);
                }
            }
            catch
            {
            }
        }

        private static string FormatMoney(decimal amount)
        {
            return "¥" + amount.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
