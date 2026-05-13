using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

class Program
{
    static string TG_TOKEN = Environment.GetEnvironmentVariable("TG_TOKEN");
    static string WEATHER_KEY = Environment.GetEnvironmentVariable("WEATHER_KEY");

    static TelegramBotClient bot;
    static HttpClient http = new HttpClient();

    // Локализация интерфейса
    static bool useKazakh = false; // переключатель языка (пока только русский)

    class UserState
    {
        public string CityEn = "Almetyevsk";
        public string CityRu = "Альметьевск";
        public bool WaitingForCustomCity;
        public bool Subscribed;
        public int MainMessageId;
        public bool FirstRun = true; // для приветствия

        // Конвертер
        public string? ConvertFrom;
        public string? ConvertTo;
        public bool WaitingForAmount;

        // Статистика
        public int RequestCount = 0;
        public double? LastTemp = null; // для уведомлений о перепадах
    }

    static ConcurrentDictionary<long, UserState> users = new();
    static ConcurrentDictionary<long, int> subscribers = new();

    static async Task Main()
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls($"http://*:{port}");
                webBuilder.Configure(app => app.Run(async context =>
                    await context.Response.WriteAsync("Bot is running")));
            })
            .Build();
        _ = host.RunAsync();

        bot = new TelegramBotClient(TG_TOKEN);
        var cts = new CancellationTokenSource();

        bot.StartReceiving(HandleUpdate, HandleError,
            new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery, UpdateType.InlineQuery }
            }, cts.Token);

        var me = await bot.GetMe();
        Console.WriteLine($"Бот @{me.Username} запущен!");

        _ = Task.Run(() => DailyNotifyLoop(cts.Token), cts.Token);

        await Task.Delay(Timeout.Infinite, cts.Token);
    }

    // ==================== ОБРАБОТКА ВСЕХ ОБНОВЛЕНИЙ ====================
    static async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        if (update.InlineQuery != null)
        {
            await HandleInlineQuery(botClient, update.InlineQuery, ct);
            return;
        }

        if (update.CallbackQuery != null)
        {
            await HandleCallback(botClient, update.CallbackQuery, ct);
            return;
        }

        if (update.Message?.Text == null) return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text.Trim();
        var user = users.GetOrAdd(chatId, _ => new UserState());

        // Статистика
        user.RequestCount++;

        // Удаляем сообщение пользователя
        try { await bot.DeleteMessage(chatId, update.Message.MessageId, ct); } catch { }

        // Команда /stats
        if (text == "/stats")
        {
            var statsMsg = $"📊 Статистика:\nЗапросов: {user.RequestCount}\nГород: {user.CityRu}\nПодписка: {(user.Subscribed ? "активна" : "нет")}";
            await EditOrSendMain(chatId, user, ct, extra: statsMsg);
            return;
        }

        // Ожидание ввода своего города
        if (user.WaitingForCustomCity)
        {
            user.WaitingForCustomCity = false;
            var (ok, _) = await TryGetWeather(text);
            if (!ok)
            {
                await EditOrSendMain(chatId, user, ct, extra: $"❌ Город \"{text}\" не найден.");
                return;
            }
            user.CityEn = text;
            user.CityRu = text;
            await EditMainMenu(chatId, user, ct);
            return;
        }

        // Ожидание ввода суммы для конвертера
        if (user.WaitingForAmount)
        {
            user.WaitingForAmount = false;
            await HandleAmountInput(chatId, user, text, ct);
            return;
        }

        // Обычное сообщение — показать главное меню
        await EditMainMenu(chatId, user, ct);
    }

    static async Task HandleCallback(ITelegramBotClient botClient, CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message.Chat.Id;
        var data = query.Data;
        var user = users.GetOrAdd(chatId, _ => new UserState());
        user.MainMessageId = query.Message.MessageId;

        // Статистика
        user.RequestCount++;

        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        switch (data)
        {
            case "refresh":
            case "back":
                await EditMainMenu(chatId, user, ct);
                break;

            case "news":
                await AnimateLoading(chatId, user, ct, async () => await EditNews(chatId, user, ct));
                break;
            case "forecast":
                await AnimateLoading(chatId, user, ct, async () => await EditForecast(chatId, user, ct));
                break;
            case "rates":
                await AnimateLoading(chatId, user, ct, async () => await EditRates(chatId, user, ct));
                break;
            case "convert":
                user.ConvertFrom = null;
                user.ConvertTo = null;
                user.WaitingForAmount = false;
                await EditConvertFromMenu(chatId, user, ct);
                break;
            case "help":
                await EditHelp(chatId, user, ct);
                break;
            case "map":
                await EditMapLink(chatId, user, ct);
                break;

            case "subscribe":
                user.Subscribed = !user.Subscribed;
                if (user.Subscribed)
                    subscribers[chatId] = GetUtcOffset(user.CityEn);
                else
                    subscribers.TryRemove(chatId, out _);
                await EditMainMenu(chatId, user, ct);
                break;

            case "city_almetyevsk": SetCity(user, "Almetyevsk", "Альметьевск"); await EditMainMenu(chatId, user, ct); break;
            case "city_shymkent":   SetCity(user, "Shymkent", "Шымкент");     await EditMainMenu(chatId, user, ct); break;
            case "city_moscow":     SetCity(user, "Moscow", "Москва");         await EditMainMenu(chatId, user, ct); break;
            case "city_almaty":     SetCity(user, "Almaty", "Алматы");         await EditMainMenu(chatId, user, ct); break;
            case "city_astana":     SetCity(user, "Astana", "Астана");         await EditMainMenu(chatId, user, ct); break;

            case "city_custom":
                user.WaitingForCustomCity = true;
                await EditOrSendMain(chatId, user, ct,
                    extra: "✏️ Введите название города на английском:",
                    keyboard: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("◀️ Отмена", "back") }
                    }));
                break;

            case "choose_city":
                await EditCityMenu(chatId, user, ct);
                break;

            // Конвертер: выбор исходной валюты
            case string s when s.StartsWith("convfrom_"):
                user.ConvertFrom = s["convfrom_".Length..];
                await EditConvertToMenu(chatId, user, ct);
                break;

            // Конвертер: выбор целевой валюты
            case string s when s.StartsWith("convto_"):
                user.ConvertTo = s["convto_".Length..];
                user.WaitingForAmount = true;
                await EditOrSendMain(chatId, user, ct,
                    extra: $"🧮 Введите сумму ({user.ConvertFrom} → {user.ConvertTo}):",
                    keyboard: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("◀️ Отмена", "back") }
                    }));
                break;
        }
    }

    static void SetCity(UserState user, string en, string ru)
    {
        user.CityEn = en;
        user.CityRu = ru;
    }

    // ==================== ГЛАВНОЕ МЕНЮ ====================
    static async Task EditMainMenu(long chatId, UserState user, CancellationToken ct)
    {
        var (weatherText, temp) = await GetWeather(user.CityEn, user.CityRu);
        var timeStr = GetLocalTimeString(user.CityEn, user.CityRu);

        // Приветствие при первом запуске
        string greeting = "";
        if (user.FirstRun)
        {
            greeting = "👋 Добро пожаловать, брат!\n\n";
            user.FirstRun = false;
        }

        // Температурная подсказка
        string tempHint = "";
        if (temp.HasValue)
        {
            if (temp <= -10) tempHint = "\n🥶 Очень холодно!";
            else if (temp >= 30) tempHint = "\n🥵 Жарко!";

            // Уведомление о резком перепаде
            if (user.LastTemp.HasValue)
            {
                var diff = Math.Abs(temp.Value - user.LastTemp.Value);
                if (diff >= 15)
                    tempHint += $"\n⚠️ Резкий перепад температуры ({diff:F0}°C)!";
            }
            user.LastTemp = temp;
        }

        var text = $"{greeting}🏙 *{user.CityRu}*\n{timeStr}\n\n{weatherText}{tempHint}\n\nВыберите действие:";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("📰 Новости", "news"),
                    InlineKeyboardButton.WithCallbackData("📆 Прогноз", "forecast") },
            new[] { InlineKeyboardButton.WithCallbackData("💵 Курсы", "rates"),
                    InlineKeyboardButton.WithCallbackData("🧮 Конвертер", "convert") },
            new[] { InlineKeyboardButton.WithCallbackData("🌍 Город", "choose_city"),
                    InlineKeyboardButton.WithCallbackData(user.Subscribed ? "🔕 Отписаться" : "🔔 Подписаться", "subscribe") },
            new[] { InlineKeyboardButton.WithCallbackData("📍 Карта", "map"),
                    InlineKeyboardButton.WithCallbackData("❓ Помощь", "help") }
        });

        await EditOrSendMessage(chatId, user, text, keyboard, ct);
    }

    // ==================== ВСПОМОГАТЕЛЬНЫЕ МЕНЮ ====================
    static async Task EditNews(long chatId, UserState user, CancellationToken ct)
    {
        var news = await GetNews(user.CityRu);
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, news, keyboard, ct);
    }

    static async Task EditForecast(long chatId, UserState user, CancellationToken ct)
    {
        var forecast = await GetForecast(user.CityEn, user.CityRu);
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, forecast, keyboard, ct);
    }

    static async Task EditRates(long chatId, UserState user, CancellationToken ct)
    {
        var rates = await GetRates();
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, rates, keyboard, ct);
    }

    static async Task EditHelp(long chatId, UserState user, CancellationToken ct)
    {
        var help = "❓ *Справка по боту:*\n\n" +
                   "• 🌤 Погода и время в выбранном городе\n" +
                   "• 📰 Новости города\n" +
                   "• 📆 Прогноз на 5 дней\n" +
                   "• 💵 Курсы валют\n" +
                   "• 🧮 Конвертер валют (с кнопками)\n" +
                   "• 🌍 Выбор города из списка или вручную\n" +
                   "• 🔔 Подписка на утреннюю погоду\n" +
                   "• 📍 Ссылка на Google Карты\n" +
                   "• 📊 Статистика по команде /stats\n\n" +
                   "Бот работает в одном сообщении и в инлайн-режиме!";
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, help, keyboard, ct);
    }

    static async Task EditMapLink(long chatId, UserState user, CancellationToken ct)
    {
        var encodedCity = Uri.EscapeDataString(user.CityRu);
        var mapUrl = $"https://www.google.com/maps?q={encodedCity}";
        var text = $"📍 *{user.CityRu}* на карте:\n[Открыть в Google Картах]({mapUrl})";
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, text, keyboard, ct);
    }

    static async Task EditCityMenu(long chatId, UserState user, CancellationToken ct)
    {
        var text = $"🌍 Текущий город: *{user.CityRu}*\nВыберите новый:";
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Альметьевск", "city_almetyevsk"),
                    InlineKeyboardButton.WithCallbackData("🇰🇿 Шымкент", "city_shymkent") },
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Москва", "city_moscow"),
                    InlineKeyboardButton.WithCallbackData("🇰🇿 Алматы", "city_almaty") },
            new[] { InlineKeyboardButton.WithCallbackData("🇰🇿 Астана", "city_astana") },
            new[] { InlineKeyboardButton.WithCallbackData("✏️ Другой", "city_custom"),
                    InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, text, keyboard, ct);
    }

    // ==================== КОНВЕРТЕР ВАЛЮТ ====================
    static async Task EditConvertFromMenu(long chatId, UserState user, CancellationToken ct)
    {
        var currencies = new[] { "RUB", "KZT", "USD", "EUR", "GBP", "CNY", "AED", "TRY", "UAH", "KGS", "UZS" };
        var buttons = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < currencies.Length; i += 3)
        {
            var row = currencies.Skip(i).Take(3).Select(c =>
                InlineKeyboardButton.WithCallbackData(c, $"convfrom_{c}")).ToArray();
            buttons.Add(row);
        }
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") });
        var keyboard = new InlineKeyboardMarkup(buttons);
        await EditOrSendMessage(chatId, user, "💱 Выберите исходную валюту:", keyboard, ct);
    }

    static async Task EditConvertToMenu(long chatId, UserState user, CancellationToken ct)
    {
        var currencies = new[] { "RUB", "KZT", "USD", "EUR", "GBP", "CNY", "AED", "TRY", "UAH", "KGS", "UZS" };
        var buttons = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < currencies.Length; i += 3)
        {
            var row = currencies.Skip(i).Take(3).Select(c =>
                InlineKeyboardButton.WithCallbackData(c, $"convto_{c}")).ToArray();
            buttons.Add(row);
        }
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") });
        var keyboard = new InlineKeyboardMarkup(buttons);
        await EditOrSendMessage(chatId, user, $"💱 {user.ConvertFrom} → ?\nВыберите целевую валюту:", keyboard, ct);
    }

    static async Task HandleAmountInput(long chatId, UserState user, string input, CancellationToken ct)
    {
        if (!decimal.TryParse(input.Trim(), out var amount) || amount <= 0)
        {
            await EditOrSendMain(chatId, user, ct, extra: "❌ Введите положительное число.");
            return;
        }

        try
        {
            var json = JObject.Parse(await http.GetStringAsync($"https://api.exchangerate-api.com/v4/latest/{user.ConvertFrom}"));
            var rate = json["rates"][user.ConvertTo]?.Value<decimal>();
            if (rate == null)
            {
                await EditOrSendMain(chatId, user, ct, extra: "❌ Ошибка: валюта не найдена.");
                return;
            }
            var result = amount * rate.Value;
            var msg = $"🧮 {amount} {user.ConvertFrom} = {result:F2} {user.ConvertTo}";
            user.ConvertFrom = null;
            user.ConvertTo = null;
            user.WaitingForAmount = false;
            await EditOrSendMain(chatId, user, ct, extra: msg);
        }
        catch
        {
            await EditOrSendMain(chatId, user, ct, extra: "❌ Ошибка при получении курса.");
        }
    }

    // ==================== ПОГОДА С ИКОНКАМИ ====================
    static async Task<(string text, string? icon)> GetWeather(string cityEn, string cityRu)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var temp = json["main"]["temp"]!.Value<double>();
            var feels = json["main"]["feels_like"]!.Value<double>();
            var desc = json["weather"][0]["description"]!.ToString();
            var hum = json["main"]["humidity"]!.Value<int>();
            var wind = json["wind"]["speed"]!.Value<double>();
            var iconCode = json["weather"][0]["icon"]?.ToString() ?? "01d";

            // Конвертация кода иконки в эмодзи
            string emoji = iconCode switch
            {
                "01d" => "☀️", "01n" => "🌙",
                "02d" => "⛅", "02n" => "🌙",
                "03d" => "☁", "03n" => "☁",
                "04d" => "☁", "04n" => "☁",
                "09d" => "🌧", "09n" => "🌧",
                "10d" => "🌦", "10n" => "🌧",
                "11d" => "⛈", "11n" => "⛈",
                "13d" => "🌨", "13n" => "🌨",
                "50d" => "🌫", "50n" => "🌫",
                _ => "🌡"
            };

            var text = $"🌤 *Погода:* {emoji} {temp:F0}°C (ощ. {feels:F0}°C)\n☁ {desc} | 💧{hum}% | 💨{wind} м/с";
            return (text, temp);
        }
        catch (Exception ex) { return ($"❌ Погода: {ex.Message}", null); }
    }

    static async Task<(bool ok, string text)> TryGetWeather(string cityEn)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            if (json["cod"]?.Value<int>() != 200) return (false, "");
            return (true, "");
        }
        catch { return (false, ""); }
    }

    static async Task<string> GetForecast(string cityEn, string cityRu)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/forecast?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var list = json["list"] as JArray;
            if (list == null) return "Прогноз не найден.";

            var daily = new Dictionary<string, (double min, double max, string desc)>();
            foreach (var item in list)
            {
                var dt = DateTime.Parse(item["dt_txt"]!.ToString());
                var day = dt.ToString("dd.MM (ddd)");
                var temp = item["main"]!["temp"]!.Value<double>();
                var desc = item["weather"]![0]!["description"]!.ToString();
                if (!daily.ContainsKey(day))
                    daily[day] = (temp, temp, desc);
                else
                {
                    var cur = daily[day];
                    daily[day] = (Math.Min(cur.min, temp), Math.Max(cur.max, temp), cur.desc);
                }
            }

            var result = $"📆 Прогноз на 5 дней — {cityRu}:\n\n";
            foreach (var d in daily.Take(5))
                result += $"📅 {d.Key}: {d.Value.min:F0}°…{d.Value.max:F0}°, {d.Value.desc}\n";
            return result;
        }
        catch (Exception ex) { return $"❌ Ошибка прогноза: {ex.Message}"; }
    }

    // ==================== НОВОСТИ ====================
    static async Task<string> GetNews(string cityRu)
    {
        try
        {
            var query = Uri.EscapeDataString(cityRu);
            var url = $"https://news.google.com/rss/search?q={query}&hl=ru&gl=RU&ceid=RU:ru";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0");
            var content = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
            var items = XDocument.Parse(content).Descendants("item");

            if (!items.Any()) return $"📰 Новостей по {cityRu} не найдено.";

            var result = $"📰 Новости {cityRu}:\n\n";
            int i = 1;
            foreach (var item in items.Take(5))
            {
                var title = item.Element("title")?.Value;
                var link = item.Element("link")?.Value;
                var date = DateTime.TryParse(item.Element("pubDate")?.Value, out var dt)
                    ? dt.ToString("dd.MM") : "";
                result += $"{i}. [{date}] {title}\n{link}\n\n";
                i++;
            }
            return result;
        }
        catch (Exception ex) { return $"❌ Ошибка новостей: {ex.Message}"; }
    }

    // ==================== КУРСЫ ВАЛЮТ ====================
    static async Task<string> GetRates()
    {
        try
        {
            var json = JObject.Parse(await http.GetStringAsync("https://api.exchangerate-api.com/v4/latest/USD"));
            var r = json["rates"]!;

            decimal Get(string code) => r[code]?.Value<decimal>() ?? 0;

            var rub = Get("RUB");
            var kzt = Get("KZT");
            var eur = Get("EUR");
            var gbp = Get("GBP");
            var cny = Get("CNY");
            var aed = Get("AED");
            var try_ = Get("TRY");
            var uah = Get("UAH");
            var kgs = Get("KGS");
            var uzs = Get("UZS");

            decimal Safe(decimal a, decimal b) => b == 0 ? 0 : a / b;

            return $"💵 Курсы валют (к USD):\n\n" +
                   $"🇷🇺 1 $ = {rub:F2} ₽\n" +
                   $"🇰🇿 1 $ = {kzt:F2} ₸\n" +
                   $"🇪🇺 1 $ = {eur:F4} €  |  1 € = {Safe(rub, eur):F2} ₽\n" +
                   $"🇬🇧 1 $ = {gbp:F4} £  |  1 £ = {Safe(rub, gbp):F2} ₽\n" +
                   $"🇨🇳 1 $ = {cny:F2} ¥\n" +
                   $"🇦🇪 1 $ = {aed:F2} د.إ\n" +
                   $"🇹🇷 1 $ = {try_:F2} ₺\n" +
                   $"🇺🇦 1 $ = {uah:F2} ₴\n" +
                   $"🇰🇬 1 $ = {kgs:F2} с\n" +
                   $"🇺🇿 1 $ = {uzs:F0} сум\n\n" +
                   $"🔁 1 ₽ = {Safe(kzt, rub):F2} ₸\n" +
                   $"🔁 1 ₸ = {Safe(rub, kzt):F4} ₽";
        }
        catch (Exception ex) { return $"❌ Ошибка курсов: {ex.Message}"; }
    }

    // ==================== АНИМАЦИЯ ЗАГРУЗКИ ====================
    static async Task AnimateLoading(long chatId, UserState user, CancellationToken ct, Func<Task> action)
    {
        if (user.MainMessageId != 0)
        {
            try
            {
                await bot.EditMessageText(chatId, user.MainMessageId, "⏳ Загрузка...", cancellationToken: ct);
                await Task.Delay(300, ct); // небольшая пауза, чтобы пользователь увидел
            }
            catch { }
        }
        await action();
    }

    // ==================== ЧАСОВЫЕ ПОЯСА ====================
    static TimeZoneInfo GetTimeZone(string cityEn)
    {
        string id = cityEn switch
        {
            "Shymkent" or "Almaty" or "Astana" => "West Asia Standard Time",
            "Moscow" => "Russian Standard Time",
            _ => "Russian Standard Time"
        };
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time"); }
    }

    static int GetUtcOffset(string cityEn) => (int)GetTimeZone(cityEn).BaseUtcOffset.TotalHours;

    static string GetLocalTimeString(string cityEn, string cityRu)
    {
        var tz = GetTimeZone(cityEn);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        return $"🕐 {local:HH:mm} | 📅 {local:dd.MM.yyyy}";
    }

    // ==================== ИНЛАЙН-РЕЖИМ ====================
    static async Task HandleInlineQuery(ITelegramBotClient botClient, InlineQuery query, CancellationToken ct)
    {
        var search = query.Query?.Trim();
        if (string.IsNullOrEmpty(search)) return;

        var (weather, _) = await GetWeather(search, search);
        var timeStr = GetLocalTimeString(search, search);
        var desc = weather.StartsWith("❌") ? "Город не найден" : $"{timeStr}\n{weather}";

        var result = new InlineQueryResultArticle(
            id: "1",
            title: $"Погода в {search}",
            inputMessageContent: new InputTextMessageContent(desc)
            {
                ParseMode = ParseMode.Markdown
            }
        );
        await botClient.AnswerInlineQuery(query.Id, new[] { result }, cacheTime: 10, cancellationToken: ct);
    }

    // ==================== РАССЫЛКА ====================
    static async Task DailyNotifyLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in subscribers)
            {
                var chatId = kv.Key;
                if (!users.TryGetValue(chatId, out var user)) continue;
                var tz = GetTimeZone(user.CityEn);
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, tz);
                if (localNow.Hour == 7 && localNow.Minute == 0)
                {
                    var (weather, _) = await GetWeather(user.CityEn, user.CityRu);
                    var time = localNow.ToString("HH:mm");
                    var msg = $"🌅 Доброе утро! Погода в {user.CityRu} на 7:00:\n{weather}";
                    try { await bot.SendMessage(chatId, msg, cancellationToken: ct); } catch { }
                }
            }
            await Task.Delay(30_000, ct);
        }
    }

    // ==================== РЕДАКТИРОВАНИЕ СООБЩЕНИЙ ====================
    static async Task EditOrSendMessage(long chatId, UserState user, string text, InlineKeyboardMarkup keyboard, CancellationToken ct)
    {
        if (user.MainMessageId != 0)
        {
            try
            {
                await bot.EditMessageText(chatId, user.MainMessageId, text,
                    parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: ct);
                return;
            }
            catch { }
        }
        var msg = await bot.SendMessage(chatId, text,
            parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: ct);
        user.MainMessageId = msg.MessageId;
    }

    static async Task EditOrSendMain(long chatId, UserState user, CancellationToken ct, string extra = "", InlineKeyboardMarkup? keyboard = null)
    {
        var (weatherText, temp) = await GetWeather(user.CityEn, user.CityRu);
        var timeStr = GetLocalTimeString(user.CityEn, user.CityRu);
        var text = $"🏙 *{user.CityRu}*\n{timeStr}\n\n{weatherText}";
        if (!string.IsNullOrEmpty(extra)) text += "\n\n" + extra;
        keyboard ??= new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, text, keyboard, ct);
    }

    // ==================== ОБРАБОТКА ОШИБОК ====================
    static Task HandleError(ITelegramBotClient botClient, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        Console.WriteLine($"Ошибка: {ex.Message}");
        return Task.CompletedTask;
    }
}
