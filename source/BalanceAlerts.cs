using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        public bool HasBaseline;
        public List<string> SpendAlerted = new List<string>();
        public Dictionary<string, bool> BalanceArmed =
            new Dictionary<string, bool>();
    }

    internal sealed class DeepSeekBalanceAlertTracker
    {
        private const decimal RechargeMinimum = 0.10m;

        private readonly string _statePath;
        private readonly string _pluginUsagePath;
        private readonly LauncherSettings _settings;
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                IncludeFields = true,
                PropertyNameCaseInsensitive = true
            };
        private BalanceAlertState _state;

        public DeepSeekBalanceAlertTracker(
            string statePath,
            string pluginUsagePath,
            LauncherSettings settings)
        {
            _statePath = statePath;
            _pluginUsagePath = pluginUsagePath;
            _settings = settings;
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
            string today = DateTime.Now.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);
            bool newDay = !String.Equals(
                    _state.Date,
                    today,
                    StringComparison.Ordinal)
                || !String.Equals(
                    _state.Currency,
                    currency,
                    StringComparison.OrdinalIgnoreCase);
            if (newDay)
            {
                _state.Date = today;
                _state.Currency = currency;
                _state.TodaySpend = 0.0m;
                _state.SpendAlerted.Clear();
                _state.HasBaseline = false;
            }

            decimal previousBalance = _state.LastBalance;
            bool hadBaseline = _state.HasBaseline;
            if (hadBaseline && result.Amount < previousBalance)
            {
                _state.TodaySpend += previousBalance - result.Amount;
            }

            decimal pluginUsage = ReadPluginUsage(today, currency);
            if (pluginUsage > _state.TodaySpend)
            {
                _state.TodaySpend = pluginUsage;
            }

            _state.LastBalance = result.Amount;
            _state.HasBaseline = true;
            update.TodaySpend = _state.TodaySpend;

            List<string> notifications = new List<string>();

            if (_settings != null
                && _settings.RechargeReminder
                && hadBaseline
                && result.Amount - previousBalance >= RechargeMinimum)
            {
                notifications.Add(
                    "充值成功：余额 "
                    + FormatMoney(result.Amount)
                    + "，本次增加 "
                    + FormatMoney(result.Amount - previousBalance));
            }

            string spendAlert = BuildSpendAlert();
            if (!String.IsNullOrEmpty(spendAlert))
            {
                notifications.Add(spendAlert);
            }

            string balanceAlert = BuildBalanceAlert(result.Amount, out bool critical);
            if (!String.IsNullOrEmpty(balanceAlert))
            {
                notifications.Add(balanceAlert);
                update.Critical = critical;
            }

            if (notifications.Count > 0)
            {
                update.Notification = String.Join(
                    Environment.NewLine,
                    notifications.ToArray());
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

        private string BuildSpendAlert()
        {
            List<decimal> crossed = new List<decimal>();
            AddSpendThreshold(
                crossed,
                "5",
                _settings != null && _settings.SpendAlert5,
                5.0m);
            AddSpendThreshold(
                crossed,
                "10",
                _settings != null && _settings.SpendAlert10,
                10.0m);
            AddSpendThreshold(
                crossed,
                "20",
                _settings != null && _settings.SpendAlert20,
                20.0m);
            AddSpendThreshold(
                crossed,
                "50",
                _settings != null && _settings.SpendAlert50,
                50.0m);
            AddSpendThreshold(
                crossed,
                "custom",
                _settings != null && _settings.SpendAlertCustom,
                _settings == null ? 15.0m : _settings.SpendCustomAmount);

            if (crossed.Count == 0)
            {
                return null;
            }

            crossed.Sort();
            List<string> labels = new List<string>();
            for (int index = 0; index < crossed.Count; index++)
            {
                labels.Add(FormatMoney(crossed[index]));
            }

            return "今日已消费 "
                + FormatMoney(_state.TodaySpend)
                + "，已达到提醒线："
                + String.Join("、", labels.ToArray());
        }

        private void AddSpendThreshold(
            List<decimal> crossed,
            string key,
            bool enabled,
            decimal threshold)
        {
            if (!enabled || threshold <= 0.0m)
            {
                return;
            }

            if (_state.TodaySpend >= threshold
                && !_state.SpendAlerted.Contains(key))
            {
                _state.SpendAlerted.Add(key);
                crossed.Add(threshold);
            }
        }

        private string BuildBalanceAlert(
            decimal amount,
            out bool critical)
        {
            List<decimal> crossed = new List<decimal>();
            decimal minimum = decimal.MaxValue;
            AddBalanceThreshold(
                crossed,
                "20",
                _settings != null && _settings.BalanceAlert20,
                20.0m,
                amount,
                ref minimum);
            AddBalanceThreshold(
                crossed,
                "10",
                _settings != null && _settings.BalanceAlert10,
                10.0m,
                amount,
                ref minimum);
            AddBalanceThreshold(
                crossed,
                "5",
                _settings != null && _settings.BalanceAlert5,
                5.0m,
                amount,
                ref minimum);
            AddBalanceThreshold(
                crossed,
                "1",
                _settings != null && _settings.BalanceAlert1,
                1.0m,
                amount,
                ref minimum);
            AddBalanceThreshold(
                crossed,
                "custom",
                _settings != null && _settings.BalanceAlertCustom,
                _settings == null ? 50.0m : _settings.BalanceCustomAmount,
                amount,
                ref minimum);

            critical = crossed.Count > 0 && minimum <= 1.0m;
            if (crossed.Count == 0)
            {
                return null;
            }

            crossed.Sort();
            List<string> labels = new List<string>();
            for (int index = 0; index < crossed.Count; index++)
            {
                labels.Add(FormatMoney(crossed[index]));
            }

            return "余额仅剩 "
                + FormatMoney(amount)
                + "，已触及提醒线："
                + String.Join("、", labels.ToArray());
        }

        private void AddBalanceThreshold(
            List<decimal> crossed,
            string key,
            bool enabled,
            decimal threshold,
            decimal amount,
            ref decimal minimum)
        {
            if (!enabled || threshold <= 0.0m)
            {
                return;
            }

            bool armed;
            if (!_state.BalanceArmed.TryGetValue(key, out armed))
            {
                armed = true;
            }

            if (amount > threshold)
            {
                _state.BalanceArmed[key] = true;
                return;
            }

            if (armed)
            {
                _state.BalanceArmed[key] = false;
                crossed.Add(threshold);
                if (threshold < minimum)
                {
                    minimum = threshold;
                }
            }
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
                if (!dateMatch.Success
                    || !amountMatch.Success
                    || !currencyMatch.Success)
                {
                    return 0.0m;
                }

                if (!String.Equals(
                        dateMatch.Groups["value"].Value,
                        today,
                        StringComparison.Ordinal)
                    || !String.Equals(
                        currencyMatch.Groups["value"].Value,
                        currency,
                        StringComparison.OrdinalIgnoreCase))
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
            return "¥" + amount.ToString(
                "0.00",
                CultureInfo.InvariantCulture);
        }
    }
}
