using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
using Microsoft.Data.Sqlite;

class Program
{
    static string TG_TOKEN = Environment.GetEnvironmentVariable("TG_TOKEN")!;
    static string WEATHER_KEY = Environment.GetEnvironmentVariable("WEATHER_KEY")!;

    static TelegramBotClient bot = null!;
    static HttpClient http = new HttpClient();
    static Database db = null!;

    static readonly Dictionary<string, string> CurrencyNames = new()
    {
        ["RUB"] = "🇷🇺 Рубль",
        ["KZT"] = "🇰🇿 Тенге",
        ["USD"] = "🇺🇸 Доллар",
        ["EUR"] = "🇪🇺 Евро",
        ["GBP"] = "🇬🇧 Фунт",
        ["CNY"] = "🇨🇳 Юань",
        ["AED"] = "🇦🇪 Дирхам",
        ["TRY"] = "🇹🇷 Лира",
        ["UAH"] = "🇺🇦 Гривна",
        ["KGS"] = "🇰🇬 Сом",
        ["UZS"] = "🇺🇿 Сум",
        ["PLN"] = "🇵🇱 Злотый"
    };

    static readonly string[] WorldCitiesEn = { "London", "Tokyo", "Berlin", "Paris", "Rome", "Madrid", "Beijing", "Sydney", "New York", "Toronto", "Seoul", "Bangkok", "Dubai", "Singapore", "Mumbai" };
    static readonly Random rng = new();

    static ConcurrentDictionary<long, UserState> users = new();
    static ConcurrentDictionary<long, int> subscribers = new();

    static async Task Main()
    {
        db = new Database();
        db.Initialize();

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

    static async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        if (update.InlineQuery != null) { await HandleInlineQuery(botClient, update.InlineQuery, ct); return; }
        if (update.CallbackQuery != null) { await HandleCallback(botClient, update.CallbackQuery, ct); return; }
        if (update.Message?.Location != null) { await HandleLocation(botClient, update.Message, ct); return; }
        if (update.Message?.Text == null) return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text.Trim();
        var user = users.GetOrAdd(chatId, _ => db.LoadUser(chatId) ?? new UserState());
        user.RequestCount++;
        try { await bot.DeleteMessage(chatId, update.Message.MessageId, ct); } catch { }

        if (text.StartsWith("/remind"))
        {
            var parts = text.Split(' ', 2);
            if (parts.Length == 2 && TimeSpan.TryParse(parts[1], out var ts))
            {
                user.RemindTime = ts.ToString(@"hh\:mm");
                db.SaveUser(chatId, user);
                await EditOrSendMain(chatId, user, ct, extra: "⏰ Напоминание установлено на " + user.RemindTime);
            }
            else await EditOrSendMain(chatId, user, ct, extra: "Формат: /remind 08:00");
            return;
        }
        if (text == "/stats")
        {
            var statsMsg = $"📊 Статистика:\nЗапросов: {user.RequestCount}\nГород: {user.CityRu}\nПодписка: {(user.Subscribed ? "активна" : "нет")}";
            await EditOrSendMain(chatId, user, ct, extra: statsMsg);
            return;
        }

        if (user.WaitingForCustomCity)
        {
            user.WaitingForCustomCity = false;
            var (ok, _) = await TryGetWeather(text);
            if (!ok) { await EditOrSendMain(chatId, user, ct, extra: $"❌ Город \"{text}\" не найден."); return; }
            user.CityEn = text; user.CityRu = text;
            db.SaveUser(chatId, user);
            await EditMainMenu(chatId, user, ct);
            return;
        }
        if (user.WaitingForAmount)
        {
            user.WaitingForAmount = false;
            await HandleAmountInput(chatId, user, text, ct);
            return;
        }
        await EditMainMenu(chatId, user, ct);
    }

    static async Task HandleLocation(ITelegramBotClient botClient, Message msg, CancellationToken ct)
    {
        var lat = msg.Location!.Latitude; var lon = msg.Location.Longitude;
        var user = users.GetOrAdd(msg.Chat.Id, _ => new UserState());
        try
        {
            var geoUrl = $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(geoUrl));
            var city = json["name"]?.ToString();
            if (!string.IsNullOrEmpty(city)) { user.CityEn = city; user.CityRu = city; db.SaveUser(msg.Chat.Id, user); await EditMainMenu(msg.Chat.Id, user, ct); }
            else await EditOrSendMain(msg.Chat.Id, user, ct, extra: "❌ Не удалось определить город.");
        }
        catch { await EditOrSendMain(msg.Chat.Id, user, ct, extra: "❌ Ошибка геолокации."); }
    }

    static async Task HandleCallback(ITelegramBotClient botClient, CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message!.Chat.Id;
        var data = query.Data!;
        var user = users.GetOrAdd(chatId, _ => new UserState());
        user.MainMessageId = query.Message.MessageId;
        user.RequestCount++;
        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        switch (data)
        {
            case "refresh": case "back": await EditMainMenu(chatId, user, ct); break;
            case "news": await AnimateLoading(chatId, user, ct, async () => await EditNews(chatId, user, ct)); break;
            case "forecast": await AnimateLoading(chatId, user, ct, async () => await EditForecast(chatId, user, ct)); break;
            case "rates": await AnimateLoading(chatId, user, ct, async () => await EditRates(chatId, user, ct)); break;
            case "convert": user.ConvertFrom = null; user.ConvertTo = null; user.WaitingForAmount = false; await EditConvertFromMenu(chatId, user, ct); break;
            case "help": await EditHelp(chatId, user, ct); break;
            case "map": await EditMapLink(chatId, user, ct); break;
            case "subscribe":
                user.Subscribed = !user.Subscribed;
                db.SaveUser(chatId, user);
                if (user.Subscribed) subscribers[chatId] = GetUtcOffset(user.CityEn);
                else subscribers.TryRemove(chatId, out _);
                await EditMainMenu(chatId, user, ct);
                break;
            case "favorite":
                if (!user.Favorites.Contains(user.CityEn)) { user.Favorites.Add(user.CityEn); if (user.Favorites.Count > 3) user.Favorites.RemoveAt(0); db.SaveFavorites(chatId, user.Favorites); }
                await EditMainMenu(chatId, user, ct);
                break;
            case "unfavorite": user.Favorites.Remove(user.CityEn); db.SaveFavorites(chatId, user.Favorites); await EditMainMenu(chatId, user, ct); break;
            case "today": await AnimateLoading(chatId, user, ct, async () => await EditToday(chatId, user, ct)); break;
            case "hourly": await AnimateLoading(chatId, user, ct, async () => await EditHourly(chatId, user, ct)); break;
            case "history": await EditHistory(chatId, user, ct); break;
            case "share": await ShareWeather(chatId, user, ct); break;

            case "city_almetyevsk": SetCity(user, "Almetyevsk", "Альметьевск", chatId); await EditMainMenu(chatId, user, ct); break;
            case "city_shymkent":   SetCity(user, "Shymkent", "Шымкент", chatId);     await EditMainMenu(chatId, user, ct); break;
            case "city_moscow":     SetCity(user, "Moscow", "Москва", chatId);         await EditMainMenu(chatId, user, ct); break;
            case "city_almaty":     SetCity(user, "Almaty", "Алматы", chatId);         await EditMainMenu(chatId, user, ct); break;
            case "city_astana":     SetCity(user, "Astana", "Астана", chatId);         await EditMainMenu(chatId, user, ct); break;
            case "city_custom":
                user.WaitingForCustomCity = true;
                await EditOrSendMain(chatId, user, ct, extra: "✏️ Введите название города на английском:",
                    keyboard: new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Отмена", "back") } }));
                break;
            case "city_random":
                var randomCity = WorldCitiesEn[rng.Next(WorldCitiesEn.Length)];
                SetCity(user, randomCity, randomCity, chatId);
                await EditMainMenu(chatId, user, ct);
                break;
            case "choose_city": await EditCityMenu(chatId, user, ct); break;

            case string s when s.StartsWith("convfrom_"):
                user.ConvertFrom = s["convfrom_".Length..];
                await EditConvertToMenu(chatId, user, ct);
                break;
            case string s when s.StartsWith("convto_"):
                user.ConvertTo = s["convto_".Length..];
                user.WaitingForAmount = true;
                await EditOrSendMain(chatId, user, ct,
                    extra: $"🧮 Введите сумму ({CurrencyName(user.ConvertFrom!)} → {CurrencyName(user.ConvertTo!)}):",
                    keyboard: new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Отмена", "back") } }));
                break;
            case string s when s.StartsWith("fav_"):
                var favCity = s[4..];
                SetCity(user, favCity, favCity, chatId);
                await EditMainMenu(chatId, user, ct);
                break;
        }
    }

    static string CurrencyName(string code) => CurrencyNames.TryGetValue(code, out var name) ? name : code;
    static void SetCity(UserState user, string en, string ru, long chatId) { user.CityEn = en; user.CityRu = ru; db.SaveUser(chatId, user); }

    static async Task EditMainMenu(long chatId, UserState user, CancellationToken ct)
    {
        var (weatherText, temp) = await GetWeather(user.CityEn, user.CityRu);
        var (sunrise, sunset) = await GetSunriseSunset(user.CityEn);
        var timeStr = GetLocalTimeString(user.CityEn, user.CityRu);

        string greeting = "";
        if (user.FirstRun) { greeting = "👋 Добро пожаловать, брат!\n\n"; user.FirstRun = false; }

        string tempHint = "";
        if (temp.HasValue)
        {
            double t = temp.Value;
            if (t <= -10) tempHint = "\n🥶 Очень холодно!";
            else if (t >= 30) tempHint = "\n🥵 Жарко!";
            if (user.LastTempDate.Date == DateTime.Now.AddDays(-1).Date && user.LastTemp.HasValue)
            {
                var diff = t - user.LastTemp.Value;
                if (Math.Abs(diff) >= 5)
                    tempHint += $"\n📈 По сравнению со вчерашним днём: {diff:+0;-0}F0°C";
            }
            user.LastTemp = t;
            user.LastTempDate = DateTime.Now;
            db.SaveUser(chatId, user);
        }

        var sunriseLine = sunrise != "" ? $"\n🌅 Восход: {sunrise}   🌇 Закат: {sunset}" : "";

        var text = $"{greeting}🏙 *{user.CityRu}*\n{timeStr}{sunriseLine}\n\n{weatherText}{tempHint}\n\nВыберите действие:";

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("📰 Новости", "news"), InlineKeyboardButton.WithCallbackData("📆 Прогноз", "forecast") },
            new[] { InlineKeyboardButton.WithCallbackData("💵 Курсы", "rates"), InlineKeyboardButton.WithCallbackData("🧮 Конвертер", "convert") },
            new[] { InlineKeyboardButton.WithCallbackData("🌍 Город", "choose_city"), InlineKeyboardButton.WithCallbackData(user.Subscribed ? "🔕 Отписаться" : "🔔 Подписаться", "subscribe") },
            new[] { InlineKeyboardButton.WithCallbackData("📍 Карта", "map"), InlineKeyboardButton.WithCallbackData("❓ Помощь", "help") },
            new[] { InlineKeyboardButton.WithCallbackData("📤 Поделиться", "share") },
        };

        if (user.Favorites.Count > 0)
        {
            var favRow = user.Favorites.Select(f => InlineKeyboardButton.WithCallbackData(f, $"fav_{f}")).ToArray();
            rows.Insert(0, favRow);
        }
        var utilRow = new List<InlineKeyboardButton>
        {
            user.Favorites.Contains(user.CityEn)
                ? InlineKeyboardButton.WithCallbackData("⭐ Убрать из избранного", "unfavorite")
                : InlineKeyboardButton.WithCallbackData("⭐ В избранное", "favorite")
        };
        rows.Add(utilRow.ToArray());

        var webAppUrl = $"https://nem1ch13.github.io/TGBot/webapp?city={Uri.EscapeDataString(user.CityEn)}";
        rows.Add(new[] { InlineKeyboardButton.WithWebApp("📊 Графики", new WebAppInfo { Url = webAppUrl }) });

        await EditOrSendMessage(chatId, user, text, new InlineKeyboardMarkup(rows), ct);
    }

    static async Task<(string sunrise, string sunset)> GetSunriseSunset(string cityEn)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var sys = json["sys"];
            if (sys == null) return ("", "");
            var sunrise = DateTimeOffset.FromUnixTimeSeconds(sys["sunrise"]!.Value<long>()).DateTime.ToLocalTime();
            var sunset = DateTimeOffset.FromUnixTimeSeconds(sys["sunset"]!.Value<long>()).DateTime.ToLocalTime();
            return (sunrise.ToString("HH:mm"), sunset.ToString("HH:mm"));
        }
        catch { return ("", ""); }
    }

    static async Task ShareWeather(long chatId, UserState user, CancellationToken ct)
    {
        var (weatherText, _) = await GetWeather(user.CityEn, user.CityRu);
        var timeStr = GetLocalTimeString(user.CityEn, user.CityRu);
        var shareText = $"Погода в {user.CityRu} сейчас:\n{timeStr}\n{weatherText}\n\nОтправлено из бота @Tgvstestbot";
        await bot.SendMessage(chatId, shareText, cancellationToken: ct);
    }

    static async Task EditHourly(long chatId, UserState user, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/forecast?q={user.CityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var list = json["list"] as JArray;
            var result = $"🕒 Почасовой прогноз для {user.CityRu}:\n\n";
            for (int i = 0; i < 8 && i < list!.Count; i++)
            {
                var item = list[i];
                var time = DateTime.Parse(item["dt_txt"]!.ToString()).ToString("HH:mm");
                var temp = item["main"]!["temp"]!.Value<double>();
                var desc = item["weather"]![0]!["description"]!.ToString();
                var icon = item["weather"]![0]!["icon"]!.ToString();
                var emoji = GetWeatherEmoji(icon);
                result += $"{time} {emoji} {temp:F0}°C, {desc}\n";
            }
            var keyboard = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "forecast") } });
            await EditOrSendMessage(chatId, user, result, keyboard, ct);
        }
        catch { await EditOrSendMessage(chatId, user, "❌ Ошибка загрузки почасового прогноза.", null!, ct); }
    }

    static string GetWeatherEmoji(string code) => code switch
    {
        "01d" => "☀️", "01n" => "🌙",
        "02d" => "⛅", "02n" => "🌙",
        "03d" or "03n" => "☁",
        "04d" or "04n" => "☁",
        "09d" or "09n" => "🌧",
        "10d" => "🌦", "10n" => "🌧",
        "11d" or "11n" => "⛈",
        "13d" or "13n" => "🌨",
        "50d" or "50n" => "🌫",
        _ => "🌡"
    };

    static async Task EditForecast(long chatId, UserState user, CancellationToken ct)
    {
        var forecast = await GetForecast(user.CityEn, user.CityRu);
        var keyboard = new InlineKeyboardMarkup(new[] {
            new[] { InlineKeyboardButton.WithCallbackData("🌤 Сегодня", "today"), InlineKeyboardButton.WithCallbackData("🕒 По часам", "hourly") },
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back"), InlineKeyboardButton.WithCallbackData("🔄 Обновить", "forecast") }
        });
        await EditOrSendMessage(chatId, user, forecast, keyboard, ct);
    }

    static async Task<string> GetForecast(string cityEn, string cityRu)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/forecast?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var list = json["list"] as JArray;
            if (list == null) return "Прогноз не найден.";

            var daily = new Dictionary<string, (double min, double max, string desc, DateTime date)>();
            foreach (var item in list)
            {
                var dt = DateTime.Parse(item["dt_txt"]!.ToString());
                var day = dt.ToString("dd.MM");
                double temp = item["main"]!["temp"]!.Value<double>();
                string desc = item["weather"]![0]!["description"]!.ToString();
                if (!daily.ContainsKey(day)) daily[day] = (temp, temp, desc, dt);
                else
                {
                    var cur = daily[day];
                    daily[day] = (Math.Min(cur.min, temp), Math.Max(cur.max, temp), cur.desc, dt);
                }
            }

            var result = $"📆 Прогноз на 5 дней — {cityRu}:\n\n";
            foreach (var d in daily.Take(5))
            {
                var dayAbbr = d.Value.date.ToString("ddd", new CultureInfo("ru-RU"));
                result += $"📅 {d.Key} ({dayAbbr}): {d.Value.min:F0}°…{d.Value.max:F0}°, {d.Value.desc}\n";
            }
            return result;
        }
        catch (Exception ex) { return $"❌ Ошибка прогноза: {ex.Message}"; }
    }

    static async Task EditNews(long chatId, UserState user, CancellationToken ct)
    {
        var news = await GetNews(user.CityRu);
        var keyboard = new InlineKeyboardMarkup(new[] {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back"), InlineKeyboardButton.WithCallbackData("🔄 Обновить", "news") }
        });
        await EditOrSendMessage(chatId, user, news, keyboard, ct);
    }

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
                var date = DateTime.TryParse(item.Element("pubDate")?.Value, out var dt) ? dt.ToString("dd.MM") : "";
                result += $"{i}. [{date}] {title}\n{link}\n\n";
                i++;
            }
            return result;
        }
        catch (Exception ex) { return $"❌ Ошибка новостей: {ex.Message}"; }
    }

    static async Task<string> GetRates()
    {
        try
        {
            var json = JObject.Parse(await http.GetStringAsync("https://api.exchangerate-api.com/v4/latest/USD"));
            var r = json["rates"]!;
            decimal Get(string code) => r[code]?.Value<decimal>() ?? 0;
            decimal rub = Get("RUB"), kzt = Get("KZT"), eur = Get("EUR"), gbp = Get("GBP"),
                    cny = Get("CNY"), aed = Get("AED"), try_ = Get("TRY"), uah = Get("UAH"),
                    kgs = Get("KGS"), uzs = Get("UZS"), pln = Get("PLN");
            decimal Safe(decimal a, decimal b) => b == 0 ? 0 : a / b;

            return "<pre>" +
                   "Валюта      │ USD        │ RUB        │ KZT        \n" +
                   "────────────┼────────────┼────────────┼────────────\n" +
                   $"🇷🇺 RUB      │ {1/rub,10:F4} │     1.0000 │ {1/(rub/kzt),10:F2}\n" +
                   $"🇰🇿 KZT      │ {1/kzt,10:F4} │ {Safe(1, rub/kzt),10:F4} │     1.0000\n" +
                   $"🇺🇸 USD      │     1.0000 │ {rub,10:F2} │ {kzt,10:F2}\n" +
                   $"🇪🇺 EUR      │ {eur,10:F4} │ {Safe(rub, eur),10:F2} │ {Safe(kzt, eur),10:F2}\n" +
                   $"🇬🇧 GBP      │ {gbp,10:F4} │ {Safe(rub, gbp),10:F2} │ {Safe(kzt, gbp),10:F2}\n" +
                   $"🇨🇳 CNY      │ {cny,10:F4} │ {Safe(rub, cny),10:F2} │ {Safe(kzt, cny),10:F2}\n" +
                   $"🇦🇪 AED      │ {aed,10:F4} │ {Safe(rub, aed),10:F2} │ {Safe(kzt, aed),10:F2}\n" +
                   $"🇹🇷 TRY      │ {try_,10:F4} │ {Safe(rub, try_),10:F2} │ {Safe(kzt, try_),10:F2}\n" +
                   $"🇺🇦 UAH      │ {uah,10:F4} │ {Safe(rub, uah),10:F2} │ {Safe(kzt, uah),10:F2}\n" +
                   $"🇰🇬 KGS      │ {kgs,10:F4} │ {Safe(rub, kgs),10:F2} │ {Safe(kzt, kgs),10:F2}\n" +
                   $"🇺🇿 UZS      │ {uzs,10:F0} │ {Safe(rub, uzs),10:F0} │ {Safe(kzt, uzs),10:F0}\n" +
                   $"🇵🇱 PLN      │ {1/pln,10:F4} │ {Safe(rub, pln),10:F2} │ {Safe(kzt, pln),10:F2}\n" +
                   "</pre>";
        }
        catch (Exception ex) { return $"❌ Ошибка курсов: {ex.Message}"; }
    }

    static async Task EditRates(long chatId, UserState user, CancellationToken ct)
    {
        var rates = await GetRates();
        var keyboard = new InlineKeyboardMarkup(new[] {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back"), InlineKeyboardButton.WithCallbackData("🔄 Обновить", "rates") }
        });
        await EditOrSendMessage(chatId, user, rates, keyboard, ct, parseMode: ParseMode.Html);
    }

    static async Task EditOrSendMessage(long chatId, UserState user, string text, InlineKeyboardMarkup keyboard, CancellationToken ct, ParseMode parseMode = ParseMode.Markdown)
    {
        if (user.MainMessageId != 0)
        {
            try
            {
                await bot.EditMessageText(chatId, user.MainMessageId, text, parseMode: parseMode, replyMarkup: keyboard, cancellationToken: ct);
                return;
            }
            catch { }
        }
        var msg = await bot.SendMessage(chatId, text, parseMode: parseMode, replyMarkup: keyboard, cancellationToken: ct);
        user.MainMessageId = msg.MessageId;
    }

    static async Task EditOrSendMain(long chatId, UserState user, CancellationToken ct, string extra = "", InlineKeyboardMarkup? keyboard = null)
    {
        var (weatherText, _) = await GetWeather(user.CityEn, user.CityRu);
        var timeStr = GetLocalTimeString(user.CityEn, user.CityRu);
        var text = $"🏙 *{user.CityRu}*\n{timeStr}\n\n{weatherText}";
        if (!string.IsNullOrEmpty(extra)) text += "\n\n" + extra;
        keyboard ??= new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") } });
        await EditOrSendMessage(chatId, user, text, keyboard, ct);
    }

    static async Task<(string text, double? temp)> GetWeather(string cityEn, string cityRu, bool useMarkdown = true)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            double temp = json["main"]!["temp"]!.Value<double>();
            double feels = json["main"]!["feels_like"]!.Value<double>();
            string desc = json["weather"]![0]!["description"]!.ToString();
            int hum = json["main"]!["humidity"]!.Value<int>();
            double wind = json["wind"]!["speed"]!.Value<double>();
            string iconCode = json["weather"]![0]!["icon"]?.ToString() ?? "01d";

            string emoji = iconCode switch
            {
                "01d" => "☀️", "01n" => "🌙",
                "02d" => "⛅", "02n" => "🌙",
                "03d" or "03n" => "☁",
                "04d" or "04n" => "☁",
                "09d" or "09n" => "🌧",
                "10d" => "🌦", "10n" => "🌧",
                "11d" or "11n" => "⛈",
                "13d" or "13n" => "🌨",
                "50d" or "50n" => "🌫",
                _ => "🌡"
            };

            string weatherLine = useMarkdown
                ? $"🌤 *Погода:* {emoji} {temp:F0}°C (ощ. {feels:F0}°C)"
                : $"🌤 Погода: {emoji} {temp:F0}°C (ощущается {feels:F0}°C)";

            string text = $"{weatherLine}\n☁ {desc} | 💧{hum}% | 💨{wind} м/с";
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
            return (json["cod"]?.Value<int>() == 200, "");
        }
        catch { return (false, ""); }
    }

    static async Task<string> GetTodayForecast(string cityEn, string cityRu)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/forecast?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var list = json["list"] as JArray;
            if (list == null) return "";

            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var todayItems = list.Where(i => i["dt_txt"]!.ToString().StartsWith(today)).ToList();
            if (!todayItems.Any()) return "📆 Прогноз на сегодня отсутствует.";

            double min = double.MaxValue, max = double.MinValue;
            string mainDesc = "";
            foreach (var item in todayItems)
            {
                double t = item["main"]!["temp"]!.Value<double>();
                if (t < min) min = t;
                if (t > max) max = t;
                mainDesc = item["weather"]![0]!["description"]!.ToString();
            }
            return $"📆 Сегодня: от {min:F0}°C до {max:F0}°C, {mainDesc}";
        }
        catch { return ""; }
    }

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
                var targetTime = user.RemindTime ?? "07:00";
                if (localNow.ToString("HH:mm") == targetTime && localNow.Date != user.LastNotifyDate)
                {
                    user.LastNotifyDate = localNow.Date;
                    var (currentWeather, _) = await GetWeather(user.CityEn, user.CityRu, useMarkdown: false);
                    var todayForecast = await GetTodayForecast(user.CityEn, user.CityRu);

                    string warning = "";
                    try
                    {
                        var fcUrl = $"https://api.openweathermap.org/data/2.5/forecast?q={user.CityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
                        var fcJson = JObject.Parse(await http.GetStringAsync(fcUrl));
                        var todayItems = (fcJson["list"] as JArray)?.Where(i => i["dt_txt"]!.ToString().StartsWith(localNow.ToString("yyyy-MM-dd")));
                        if (todayItems != null && todayItems.Any())
                        {
                            double maxWind = todayItems.Max(i => i["wind"]!["speed"]!.Value<double>());
                            bool heavyRain = todayItems.Any(i => i["weather"]![0]!["main"]!.ToString() == "Rain" && i["weather"]![0]!["id"]!.Value<int>() >= 502);
                            if (maxWind > 15) warning += "\n⚠️ Сегодня ожидается сильный ветер!";
                            if (heavyRain) warning += "\n⚠️ Ожидается сильный дождь!";
                        }
                    }
                    catch { }

                    var message = $"🌅 Доброе утро! Погода в {user.CityRu} на {localNow:dd.MM.yyyy}:\n\n" +
                                  $"{currentWeather}\n" +
                                  $"{todayForecast}" +
                                  $"{warning}";
                    try { await bot.SendMessage(chatId, message, cancellationToken: ct); } catch { }
                }
            }
            await Task.Delay(30_000, ct);
        }
    }

    static async Task EditToday(long chatId, UserState user, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/forecast?q={user.CityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var list = json["list"] as JArray;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var todayItems = list!.Where(i => i["dt_txt"]!.ToString().StartsWith(today)).ToList();

            var result = $"🌤 Погода в {user.CityRu} на сегодня:\n\n";
            var times = new[] { "06:00", "12:00", "18:00", "21:00" };
            foreach (var t in times)
            {
                var item = todayItems.FirstOrDefault(i => i["dt_txt"]!.ToString().Contains(t));
                if (item != null)
                {
                    var temp = item["main"]!["temp"]!.Value<double>();
                    var desc = item["weather"]![0]!["description"]!.ToString();
                    result += $"🕐 {t}: {temp:F0}°C, {desc}\n";
                }
            }
            if (!todayItems.Any()) result += "Нет данных.";
            var keyboard = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "forecast") } });
            await EditOrSendMessage(chatId, user, result, keyboard, ct);
        }
        catch { await EditOrSendMessage(chatId, user, "❌ Ошибка загрузки.", null!, ct); }
    }

    static async Task EditHelp(long chatId, UserState user, CancellationToken ct)
    {
        var help = "❓ *Справка:*\n\n• 🌤 Погода и время\n• 📰 Новости\n• 📆 Прогноз на 5 дней\n• 💵 Курсы валют\n• 🧮 Конвертер\n• 🌍 Выбор города\n• 🔔 Подписка\n• 📍 Карта\n• 📊 /stats\n• ⏰ /remind 08:00";
        var keyboard = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") } });
        await EditOrSendMessage(chatId, user, help, keyboard, ct);
    }

    static async Task EditMapLink(long chatId, UserState user, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(user.CityRu);
        var mapUrl = $"https://www.google.com/maps?q={encoded}";
        var text = $"📍 *{user.CityRu}* на карте:\n[Открыть в Google Картах]({mapUrl})";
        var keyboard = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") } });
        await EditOrSendMessage(chatId, user, text, keyboard, ct);
    }

    static async Task EditCityMenu(long chatId, UserState user, CancellationToken ct)
    {
        var text = $"🌍 Текущий город: *{user.CityRu}*\nВыберите новый:";
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Альметьевск", "city_almetyevsk"), InlineKeyboardButton.WithCallbackData("🇰🇿 Шымкент", "city_shymkent") },
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Москва", "city_moscow"), InlineKeyboardButton.WithCallbackData("🇰🇿 Алматы", "city_almaty") },
            new[] { InlineKeyboardButton.WithCallbackData("🇰🇿 Астана", "city_astana") },
            new[] { InlineKeyboardButton.WithCallbackData("🎲 Случайный", "city_random"), InlineKeyboardButton.WithCallbackData("✏️ Другой", "city_custom") },
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, text, keyboard, ct);
    }

    static async Task EditConvertFromMenu(long chatId, UserState user, CancellationToken ct)
    {
        var currencies = new[] { "RUB", "KZT", "USD", "EUR", "GBP", "PLN", "CNY", "AED", "TRY", "UAH", "KGS", "UZS" };
        var buttons = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < currencies.Length; i += 3)
            buttons.Add(currencies.Skip(i).Take(3).Select(c => InlineKeyboardButton.WithCallbackData(CurrencyName(c), $"convfrom_{c}")).ToArray());
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("📋 История", "history"), InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") });
        await EditOrSendMessage(chatId, user, "💱 Выберите исходную валюту:", new InlineKeyboardMarkup(buttons), ct);
    }

    static async Task EditConvertToMenu(long chatId, UserState user, CancellationToken ct)
    {
        var currencies = new[] { "RUB", "KZT", "USD", "EUR", "GBP", "PLN", "CNY", "AED", "TRY", "UAH", "KGS", "UZS" };
        var buttons = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < currencies.Length; i += 3)
            buttons.Add(currencies.Skip(i).Take(3).Select(c => InlineKeyboardButton.WithCallbackData(CurrencyName(c), $"convto_{c}")).ToArray());
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") });
        await EditOrSendMessage(chatId, user, $"💱 {CurrencyName(user.ConvertFrom!)} → ?\nВыберите целевую валюту:", new InlineKeyboardMarkup(buttons), ct);
    }

    static async Task EditHistory(long chatId, UserState user, CancellationToken ct)
    {
        var hist = user.ConversionHistory.Count > 0 ? string.Join("\n", user.ConversionHistory) : "История пуста.";
        var keyboard = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "convert") } });
        await EditOrSendMessage(chatId, user, $"📋 Последние конвертации:\n{hist}", keyboard, ct);
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
            var rate = json["rates"]![user.ConvertTo!]?.Value<decimal>();
            if (rate == null) { await EditOrSendMain(chatId, user, ct, extra: "❌ Валюта не найдена."); return; }
            var result = amount * rate.Value;
            var entry = $"{amount} {CurrencyName(user.ConvertFrom!)} = {result:F2} {CurrencyName(user.ConvertTo!)}";
            user.ConversionHistory.Insert(0, entry);
            if (user.ConversionHistory.Count > 5) user.ConversionHistory.RemoveAt(5);
            await EditOrSendMain(chatId, user, ct, extra: "🧮 " + entry);
            user.ConvertFrom = null; user.ConvertTo = null; user.WaitingForAmount = false;
        }
        catch { await EditOrSendMain(chatId, user, ct, extra: "❌ Ошибка курса."); }
    }

    static TimeZoneInfo GetTimeZone(string cityEn)
    {
        string id = cityEn switch
        {
            "Shymkent" or "Almaty" or "Astana" => "West Asia Standard Time",
            "Moscow" => "Russian Standard Time",
            _ => "Russian Standard Time"
        };
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time"); }
    }

    static int GetUtcOffset(string cityEn) => (int)GetTimeZone(cityEn).BaseUtcOffset.TotalHours;

    static string GetLocalTimeString(string cityEn, string cityRu)
    {
        var tz = GetTimeZone(cityEn);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        return $"🕐 {local:HH:mm} | 📅 {local:dd.MM.yyyy}";
    }

    static async Task HandleInlineQuery(ITelegramBotClient botClient, InlineQuery query, CancellationToken ct)
    {
        var search = query.Query?.Trim();
        if (string.IsNullOrEmpty(search)) return;
        var (weather, _) = await GetWeather(search, search);
        var timeStr = GetLocalTimeString(search, search);
        var desc = weather.StartsWith("❌") ? "Город не найден" : $"{timeStr}\n{weather}";
        var result = new InlineQueryResultArticle("1", $"Погода в {search}", new InputTextMessageContent(desc) { ParseMode = ParseMode.Markdown });
        await botClient.AnswerInlineQuery(query.Id, new[] { result }, cacheTime: 10, cancellationToken: ct);
    }

    static async Task AnimateLoading(long chatId, UserState user, CancellationToken ct, Func<Task> action)
    {
        if (user.MainMessageId != 0)
            try { await bot.EditMessageText(chatId, user.MainMessageId, "⏳ Загрузка...", cancellationToken: ct); await Task.Delay(300, ct); } catch { }
        await action();
    }

    static Task HandleError(ITelegramBotClient botClient, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        Console.WriteLine($"Ошибка: {ex.Message}");
        return Task.CompletedTask;
    }
}

// ==================== КЛАССЫ ВНЕ Program ====================

class UserState
{
    public string CityEn = "Almetyevsk";
    public string CityRu = "Альметьевск";
    public bool WaitingForCustomCity;
    public bool Subscribed;
    public int MainMessageId;
    public bool FirstRun = true;

    public string? ConvertFrom;
    public string? ConvertTo;
    public bool WaitingForAmount;

    public int RequestCount;
    public double? LastTemp;
    public DateTime LastTempDate;

    public List<string> Favorites = new();
    public List<string> ConversionHistory = new();
    public string? RemindTime;
    public DateTime LastNotifyDate;
}

class Database
{
    readonly string connectionString = "Data Source=/data/bot.db";

    public void Initialize()
    {
        Directory.CreateDirectory("/data"); // создаём папку, если нет
        using var con = new SqliteConnection(connectionString);
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Users (
                ChatId INTEGER PRIMARY KEY,
                CityEn TEXT,
                CityRu TEXT,
                Subscribed INTEGER,
                RemindTime TEXT,
                LastTemp REAL,
                LastTempDate TEXT
            );
            CREATE TABLE IF NOT EXISTS Favorites (
                ChatId INTEGER,
                CityEn TEXT,
                PRIMARY KEY (ChatId, CityEn)
            );
            CREATE TABLE IF NOT EXISTS ConversionHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ChatId INTEGER,
                EntryText TEXT,
                CreatedAt TEXT
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public UserState? LoadUser(long chatId)
    {
        using var con = new SqliteConnection(connectionString);
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT CityEn, CityRu, Subscribed, RemindTime, LastTemp, LastTempDate FROM Users WHERE ChatId = @id";
        cmd.Parameters.AddWithValue("@id", chatId);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var st = new UserState
            {
                CityEn = reader.GetString(0),
                CityRu = reader.GetString(1),
                Subscribed = reader.GetInt32(2) == 1,
                RemindTime = reader.IsDBNull(3) ? null : reader.GetString(3),
                LastTemp = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                LastTempDate = reader.IsDBNull(5) ? DateTime.MinValue : DateTime.Parse(reader.GetString(5))
            };
            cmd.CommandText = "SELECT CityEn FROM Favorites WHERE ChatId = @id";
            using var favReader = cmd.ExecuteReader();
            while (favReader.Read()) st.Favorites.Add(favReader.GetString(0));
            return st;
        }
        return null;
    }

    public void SaveUser(long chatId, UserState user)
    {
        using var con = new SqliteConnection(connectionString);
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO Users (ChatId, CityEn, CityRu, Subscribed, RemindTime, LastTemp, LastTempDate)
            VALUES (@id, @cityEn, @cityRu, @sub, @remind, @temp, @tempDate)";
        cmd.Parameters.AddWithValue("@id", chatId);
        cmd.Parameters.AddWithValue("@cityEn", user.CityEn);
        cmd.Parameters.AddWithValue("@cityRu", user.CityRu);
        cmd.Parameters.AddWithValue("@sub", user.Subscribed ? 1 : 0);
        cmd.Parameters.AddWithValue("@remind", (object?)user.RemindTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@temp", (object?)user.LastTemp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tempDate", user.LastTempDate == DateTime.MinValue ? DBNull.Value : user.LastTempDate.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void SaveFavorites(long chatId, List<string> favorites)
    {
        using var con = new SqliteConnection(connectionString);
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM Favorites WHERE ChatId = @id";
        cmd.Parameters.AddWithValue("@id", chatId);
        cmd.ExecuteNonQuery();
        foreach (var fav in favorites)
        {
            cmd.CommandText = "INSERT INTO Favorites (ChatId, CityEn) VALUES (@id, @city)";
            cmd.Parameters.AddWithValue("@city", fav);
            cmd.ExecuteNonQuery();
        }
    }
}
