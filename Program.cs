using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

class Program
{
    static string TG_TOKEN = Environment.GetEnvironmentVariable("TG_TOKEN");
    static string WEATHER_KEY = Environment.GetEnvironmentVariable("WEATHER_KEY");

    static TelegramBotClient bot;
    static HttpClient http = new HttpClient();

    static string currentCity = "Almetyevsk";
    static string currentCityRu = "Альметьевск";
    static bool waitingForCity = false;

    static async Task Main()
    {
        bot = new TelegramBotClient(TG_TOKEN);

        var cts = new CancellationTokenSource();
        bot.StartReceiving(
            HandleUpdate,
            HandleError,
            new ReceiverOptions { AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery } },
            cts.Token
        );

        var me = await bot.GetMe();
        Console.WriteLine($"Бот @{me.Username} запущен!");
        Console.ReadLine();
        cts.Cancel();
    }

    static async Task HandleUpdate(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.CallbackQuery != null)
        {
            await HandleCallback(bot, update.CallbackQuery, ct);
            return;
        }

        if (update.Message?.Text == null) return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text.Trim();

        if (waitingForCity)
        {
            currentCity = text;
            currentCityRu = text;
            waitingForCity = false;
            await bot.SendMessage(chatId, $"✅ Выбран город: {text}", cancellationToken: ct);
            await SendMainMenu(chatId, ct);
            return;
        }

        await SendMainMenu(chatId, ct);
    }

    static async Task HandleCallback(ITelegramBotClient bot, CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message.Chat.Id;
        var data = query.Data;

        await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        if (data == "weather")
        {
            var msg = await GetWeather();
            await bot.SendMessage(chatId, msg, cancellationToken: ct);
            await SendMainMenu(chatId, ct);
        }
        else if (data == "time")
        {
            var msg = GetTime();
            await bot.SendMessage(chatId, msg, cancellationToken: ct);
            await SendMainMenu(chatId, ct);
        }
        else if (data == "news")
        {
            var msg = await GetNews();
            await bot.SendMessage(chatId, msg, cancellationToken: ct);
            await SendMainMenu(chatId, ct);
        }
        else if (data == "choose_city")
        {
            await SendCityMenu(chatId, ct);
        }
        else if (data == "city_almetyevsk")
        {
            currentCity = "Almetyevsk";
            currentCityRu = "Альметьевск";
            await bot.SendMessage(chatId, "✅ Выбран город: Альметьевск 🇷🇺", cancellationToken: ct);
            await SendMainMenu(chatId, ct);
        }
        else if (data == "city_shymkent")
        {
            currentCity = "Shymkent";
            currentCityRu = "Шымкент";
            await bot.SendMessage(chatId, "✅ Выбран город: Шымкент 🇰🇿", cancellationToken: ct);
            await SendMainMenu(chatId, ct);
        }
        else if (data == "city_custom")
        {
            waitingForCity = true;
            await bot.SendMessage(chatId,
                "✏️ Напиши название города на английском языке\nНапример: Moscow, London, Paris",
                cancellationToken: ct);
        }
        else if (data == "back")
        {
            await SendMainMenu(chatId, ct);
        }
    }

    static async Task SendMainMenu(long chatId, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🌤 Погода", "weather"),
                InlineKeyboardButton.WithCallbackData("🕐 Время", "time"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📰 Новости", "news"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🌍 Выбрать город", "choose_city"),
            }
        });

        await bot.SendMessage(chatId,
            $"🏙 Текущий город: *{currentCityRu}*\n\nВыбери действие:",
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    static async Task SendCityMenu(long chatId, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🇷🇺 Альметьевск", "city_almetyevsk"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🇰🇿 Шымкент", "city_shymkent"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✏️ Другой город", "city_custom"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("◀️ Назад", "back"),
            }
        });

        await bot.SendMessage(chatId, "🌍 Выбери город:", replyMarkup: keyboard, cancellationToken: ct);
    }

    static async Task<string> GetWeather()
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={currentCity}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var response = await http.GetStringAsync(url);
            var json = JObject.Parse(response);

            var temp = json["main"]["temp"];
            var feels = json["main"]["feels_like"];
            var desc = json["weather"][0]["description"];
            var humidity = json["main"]["humidity"];
            var wind = json["wind"]["speed"];

            return $"🌤 Погода в {currentCityRu}:\n\n" +
                   $"🌡 Температура: {temp:F0}°C\n" +
                   $"🤔 Ощущается как: {feels:F0}°C\n" +
                   $"💧 Влажность: {humidity}%\n" +
                   $"💨 Ветер: {wind} м/с\n" +
                   $"☁ {desc}";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка погоды: {ex.Message}";
        }
    }

    static string GetTime()
    {
        string tzId;
        if (currentCity == "Shymkent")
            tzId = "West Asia Standard Time"; // UTC+5
        else
            tzId = "Russian Standard Time"; // UTC+3

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            var time = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            return $"🕐 Время в {currentCityRu}:\n\n{time:HH:mm:ss}\n📅 {time:dd.MM.yyyy}";
        }
        catch
        {
            var time = DateTime.UtcNow.AddHours(3);
            return $"🕐 Время в {currentCityRu}:\n\n{time:HH:mm:ss}\n📅 {time:dd.MM.yyyy}";
        }
    }

    static async Task<string> GetNews()
    {
        try
        {
            var query = Uri.EscapeDataString(currentCityRu);
            var url = $"https://news.google.com/rss/search?q={query}&hl=ru&gl=RU&ceid=RU:ru";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var response = await http.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            var xml = XDocument.Parse(content);
            var items = xml.Descendants("item");

            if (!items.Any())
                return $"📰 Новостей по {currentCityRu} не найдено.";

            var result = $"📰 Новости {currentCityRu}:\n\n";
            int i = 1;
            foreach (var item in items.Take(5))
            {
                var title = item.Element("title")?.Value;
                var link = item.Element("link")?.Value;
                var pubDate = item.Element("pubDate")?.Value;
                string date = "";
                if (DateTime.TryParse(pubDate, out var dt))
                    date = dt.ToString("dd.MM");
                result += $"{i}. [{date}] {title}\n{link}\n\n";
                i++;
            }
            return result;
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка новостей: {ex.Message}";
        }
    }

    static Task HandleError(ITelegramBotClient bot, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        Console.WriteLine($"Ошибка: {ex.Message}");
        return Task.CompletedTask;
    }
}
