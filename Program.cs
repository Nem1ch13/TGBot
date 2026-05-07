using Newtonsoft.Json.Linq;
using System;
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

    static string currentCity = "Almetyevsk";
    static string currentCityRu = "Альметьевск";
    static bool waitingForCity = false;

    // Храним ID последнего сообщения бота (не главного меню) для удаления
    static Dictionary<long, int> lastBotMessageId = new Dictionary<long, int>();
    // Храним ID и FileId фото главного меню для редактирования
    static Dictionary<long, (int MessageId, string? PhotoFileId)> mainMenuMessages = new();

    // Словарь с картинками городов (добавил много новых)
    static Dictionary<string, CityImages> cityImages = new Dictionary<string, CityImages>
    {
        ["Almetyevsk"] = new CityImages
        {
            Day    = "https://ic.pics.livejournal.com/zdorovs/16627846/1742999/1742999_original.jpg",
            Evening = "https://ic.pics.livejournal.com/zdorovs/16627846/1749257/1749257_original.jpg",
            Night  = "https://photocentra.ru/images/main28/285795_main.jpg"
        },
        ["Shymkent"] = new CityImages
        {
            Day    = "https://avatars.mds.yandex.net/i?id=23b3468cd84a555f5ad7feecb4f9fbef_l-5288220-images-thumbs&n=13",
            Evening = "https://informburo.kz/storage/photos/oldArticle/main/SRhiOULsnSUvifkd.jpg",
            Night  = "https://i.ytimg.com/vi/F1PKeSICD-c/maxresdefault.jpg"
        },
        // Новые города (фоны подобраны из надёжных источников)
        ["Moscow"] = new CityImages
        {
            Day     = "https://images.unsplash.com/photo-1512495039889-59f5c85e3d47?w=800",
            Evening = "https://images.unsplash.com/photo-1512061943748-51e8b0d12a8b?w=800",
            Night   = "https://images.unsplash.com/photo-1506665531195-42ddc3e9c7c9?w=800"
        },
        ["Saint Petersburg"] = new CityImages
        {
            Day     = "https://images.unsplash.com/photo-1598618216261-b0d1c5aed8ee?w=800",
            Evening = "https://images.unsplash.com/photo-1570129477492-45c003edd2be?w=800",
            Night   = "https://images.unsplash.com/photo-1544003627-b97ca69a2b2e?w=800"
        },
        ["Kazan"] = new CityImages
        {
            Day     = "https://images.unsplash.com/photo-1589569260856-80f4c56011d2?w=800",
            Evening = "https://images.unsplash.com/photo-1548346481-3d3a03d3fd0b?w=800",
            Night   = "https://images.unsplash.com/photo-1575065022816-f7b1d8b5bde9?w=800"
        },
        ["Istanbul"] = new CityImages
        {
            Day     = "https://images.unsplash.com/photo-1524231757912-21f4fe3a7200?w=800",
            Evening = "https://images.unsplash.com/photo-1503676260728-1c00da094a0b?w=800",
            Night   = "https://images.unsplash.com/photo-1541432901042-2d4bf86cd438?w=800"
        },
        ["Almaty"] = new CityImages
        {
            Day     = "https://images.unsplash.com/photo-1589733955747-6d774947a6af?w=800",
            Evening = "https://images.unsplash.com/photo-1567529662178-1b13c7501013?w=800",
            Night   = "https://images.unsplash.com/photo-1520095328616-0666d3bdb6e5?w=800"
        },
        ["default"] = new CityImages
        {
            Day    = "https://i.ibb.co/0jQp5vL/default-day.jpg",
            Evening = "https://i.ibb.co/v4p9v0w/default-evening.jpg",
            Night  = "https://i.ibb.co/WDfT9YK/default-night.jpg"
        }
    };

    class CityImages
    {
        public string? Day { get; set; }
        public string? Evening { get; set; }
        public string? Night { get; set; }
    }

    static async Task Main()
    {
        // Веб-сервер для Railway healthcheck
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls($"http://*:{port}");
                webBuilder.Configure(app =>
                {
                    app.Run(async context =>
                    {
                        await context.Response.WriteAsync("Bot is running");
                    });
                });
            })
            .Build();

        _ = host.RunAsync();

        // Запуск бота
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

        await Task.Delay(Timeout.Infinite, cts.Token);
    }

    static async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        if (update.CallbackQuery != null)
        {
            await HandleCallback(botClient, update.CallbackQuery, ct);
            return;
        }

        if (update.Message?.Text == null) return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text.Trim();

        // Удаляем сообщение пользователя, чтобы не засорять чат
        try { await bot.DeleteMessage(chatId, update.Message.MessageId, ct); } catch { }

        if (waitingForCity)
        {
            currentCity = text;
            currentCityRu = text;
            waitingForCity = false;
        }

        await SendMainMenu(chatId, ct);
    }

    static async Task HandleCallback(ITelegramBotClient botClient, CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message.Chat.Id;
        var data = query.Data;

        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        // Нажатие "Назад" в дополнительных сообщениях
        if (data == "back")
        {
            await DeleteLastMessage(chatId, ct);
            await SendMainMenu(chatId, ct);
            return;
        }

        // Новости, прогноз, курсы отправляем как отдельное сообщение с кнопкой Назад
        if (data == "news" || data == "forecast" || data == "rates")
        {
            string msgText = data switch
            {
                "news" => await GetNews(),
                "forecast" => await GetForecast(),
                "rates" => await GetRates(),
                _ => "Неизвестная команда"
            };

            await DeleteLastMessage(chatId, ct); // удаляем предыдущее доп. сообщение, если было
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
            });
            var sent = await bot.SendMessage(chatId, msgText,
                replyMarkup: keyboard, cancellationToken: ct);
            lastBotMessageId[chatId] = sent.MessageId;
            return;
        }

        // Выбор города из меню
        if (data == "choose_city")
        {
            await SendCityMenu(chatId, ct);
            return;
        }
        if (data == "city_almetyevsk") { SetCity("Almetyevsk", "Альметьевск"); }
        else if (data == "city_shymkent") { SetCity("Shymkent", "Шымкент"); }
        else if (data == "city_moscow") { SetCity("Moscow", "Москва"); }
        else if (data == "city_spb") { SetCity("Saint Petersburg", "Санкт-Петербург"); }
        else if (data == "city_kazan") { SetCity("Kazan", "Казань"); }
        else if (data == "city_istanbul") { SetCity("Istanbul", "Стамбул"); }
        else if (data == "city_almaty") { SetCity("Almaty", "Алматы"); }
        else if (data == "city_custom")
        {
            waitingForCity = true;
            await DeleteLastMessage(chatId, ct);
            var sent = await bot.SendMessage(chatId,
                "✏️ Напиши название города на английском языке\nНапример: Moscow, London, Paris",
                cancellationToken: ct);
            lastBotMessageId[chatId] = sent.MessageId;
            return;
        }
        else return;

        // После смены города обновляем главное меню
        await SendMainMenu(chatId, ct);
    }

    static void SetCity(string eng, string ru)
    {
        currentCity = eng;
        currentCityRu = ru;
    }

    static async Task DeleteLastMessage(long chatId, CancellationToken ct)
    {
        if (lastBotMessageId.TryGetValue(chatId, out var msgId))
        {
            try { await bot.DeleteMessage(chatId, msgId, ct); } catch { }
            lastBotMessageId.Remove(chatId);
        }
    }

    // Главное меню с редактированием
    static async Task SendMainMenu(long chatId, CancellationToken ct, bool editIfPossible = true)
    {
        // Пробуем отредактировать существующее главное сообщение
        if (editIfPossible && mainMenuMessages.TryGetValue(chatId, out var oldMsg))
        {
            try
            {
                var (weatherText, _) = await GetWeather();
                var timeString = GetTime();
                var timeOfDay = GetTimeOfDay();
                var caption = $"🏙 *{currentCityRu}*\n{timeString}\n{weatherText}\n\nВыберите действие:";
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("📰 Новости", "news"),
                            InlineKeyboardButton.WithCallbackData("📆 Прогноз", "forecast") },
                    new[] { InlineKeyboardButton.WithCallbackData("💵 Курсы", "rates"),
                            InlineKeyboardButton.WithCallbackData("🌍 Выбрать город", "choose_city") }
                });

                if (oldMsg.PhotoFileId != null)
                {
                    await bot.EditMessageCaption(chatId, oldMsg.MessageId, caption,
                        parseMode: ParseMode.Markdown,
                        replyMarkup: keyboard,
                        cancellationToken: ct);
                }
                else
                {
                    await bot.EditMessageText(chatId, oldMsg.MessageId, caption,
                        parseMode: ParseMode.Markdown,
                        replyMarkup: keyboard,
                        cancellationToken: ct);
                }
                return;
            }
            catch { /* не удалось отредактировать — отправляем новое */ }
        }

        // Отправка нового главного сообщения (с фото или без)
        var (weatherTextNew, _) = await GetWeather();
        var timeStringNew = GetTime();
        var timeOfDayNew = GetTimeOfDay();
        var captionNew = $"🏙 *{currentCityRu}*\n{timeStringNew}\n{weatherTextNew}\n\nВыберите действие:";
        var keyboardNew = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("📰 Новости", "news"),
                    InlineKeyboardButton.WithCallbackData("📆 Прогноз", "forecast") },
            new[] { InlineKeyboardButton.WithCallbackData("💵 Курсы", "rates"),
                    InlineKeyboardButton.WithCallbackData("🌍 Выбрать город", "choose_city") }
        });

        string? imageUrl = GetCityImageUrl(currentCity, timeOfDayNew);
        Message? sent = null;

        if (!string.IsNullOrEmpty(imageUrl))
        {
            try
            {
                sent = await bot.SendPhoto(chatId, InputFile.FromUri(imageUrl),
                    caption: captionNew,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: keyboardNew,
                    cancellationToken: ct);
                mainMenuMessages[chatId] = (sent.MessageId, sent.Photo?.FirstOrDefault()?.FileId);
            }
            catch
            {
                sent = await bot.SendMessage(chatId, captionNew,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: keyboardNew,
                    cancellationToken: ct);
                mainMenuMessages[chatId] = (sent.MessageId, null);
            }
        }
        else
        {
            sent = await bot.SendMessage(chatId, captionNew,
                parseMode: ParseMode.Markdown,
                replyMarkup: keyboardNew,
                cancellationToken: ct);
            mainMenuMessages[chatId] = (sent.MessageId, null);
        }
    }

    static async Task SendCityMenu(long chatId, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Альметьевск", "city_almetyevsk"),
                    InlineKeyboardButton.WithCallbackData("🇰🇿 Шымкент", "city_shymkent") },
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Москва", "city_moscow"),
                    InlineKeyboardButton.WithCallbackData("🇷🇺 Санкт-Петербург", "city_spb") },
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Казань", "city_kazan"),
                    InlineKeyboardButton.WithCallbackData("🇹🇷 Стамбул", "city_istanbul") },
            new[] { InlineKeyboardButton.WithCallbackData("🇰🇿 Алматы", "city_almaty") },
            new[] { InlineKeyboardButton.WithCallbackData("✏️ Другой город", "city_custom") },
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });

        var sent = await bot.SendMessage(chatId, "🌍 Выбери город:", replyMarkup: keyboard, cancellationToken: ct);
        lastBotMessageId[chatId] = sent.MessageId;
    }

    // ----------------- Время и таймзоны -----------------
    static DateTime GetLocalDateTime()
    {
        string tzId = currentCity switch
        {
            "Shymkent" or "Almaty" => "West Asia Standard Time",       // UTC+5
            "Moscow" or "Saint Petersburg" or "Kazan" => "Russian Standard Time", // UTC+3
            "Istanbul" => "Turkey Standard Time",                      // UTC+3
            _ => "Russian Standard Time"                               // Альметьевск и др.
        };
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch
        {
            int offset = currentCity switch
            {
                "Shymkent" or "Almaty" => 5,
                _ => 3
            };
            return DateTime.UtcNow.AddHours(offset);
        }
    }

    static string GetTime()
    {
        var localTime = GetLocalDateTime();
        return $"🕐 Время: {localTime:HH:mm}\n📅 {localTime:dd.MM.yyyy}";
    }

    static string GetTimeOfDay()
    {
        var hour = GetLocalDateTime().Hour;
        if (hour >= 5 && hour < 12) return "evening";   // утро/вечер
        if (hour >= 12 && hour < 18) return "day";      // день
        return "night";                                  // ночь
    }

    static string? GetCityImageUrl(string city, string timeOfDay)
    {
        var images = cityImages.ContainsKey(city) ? cityImages[city] : cityImages["default"];
        return timeOfDay switch
        {
            "day" => images.Day,
            "evening" => images.Evening,
            "night" => images.Night,
            _ => null
        };
    }

    // ----------------- Погода -----------------
    static async Task<(string text, string? iconUrl)> GetWeather()
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
            var icon = json["weather"][0]["icon"]?.ToString();

            string? iconUrl = !string.IsNullOrEmpty(icon)
                ? $"https://openweathermap.org/img/wn/{icon}@2x.png"
                : null;

            var text = $"🌤 Погода:\n" +
                       $"🌡 {temp:F0}°C (ощущается {feels:F0}°C)\n" +
                       $"💧 {humidity}% | 💨 {wind} м/с | ☁ {desc}";

            return (text, iconUrl);
        }
        catch (Exception ex)
        {
            return ($"❌ Погода недоступна: {ex.Message}", null);
        }
    }

    // ----------------- Прогноз на 3 дня -----------------
    static async Task<string> GetForecast()
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/forecast?q={currentCity}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var list = json["list"] as JArray;
            if (list == null || list.Count == 0) return "Прогноз не найден.";

            var daily = new Dictionary<string, (double min, double max, string desc)>();
            foreach (var item in list)
            {
                var dt = DateTime.Parse(item["dt_txt"].ToString());
                var day = dt.ToString("dd.MM (ddd)");
                var temp = item["main"]["temp"].Value<double>();
                var desc = item["weather"][0]["description"].ToString();
                if (!daily.ContainsKey(day))
                    daily[day] = (temp, temp, desc);
                else
                {
                    var cur = daily[day];
                    daily[day] = (Math.Min(cur.min, temp), Math.Max(cur.max, temp), cur.desc);
                }
            }

            var result = $"📆 Прогноз в {currentCityRu}:\n\n";
            foreach (var d in daily.Take(3))
            {
                result += $"{d.Key}: {d.Value.min:F0}°…{d.Value.max:F0}°, {d.Value.desc}\n";
            }
            return result;
        }
        catch (Exception ex) { return $"❌ Ошибка прогноза: {ex.Message}"; }
    }

    // ----------------- Новости -----------------
    static async Task<string> GetNews()
    {
        try
        {
            var query = Uri.EscapeDataString(currentCityRu);
            var url = $"https://news.google.com/rss/search?q={query}&hl=ru&gl=RU&ceid=RU:ru";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0");
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
        catch (Exception ex) { return $"❌ Ошибка новостей: {ex.Message}"; }
    }

    // ----------------- Курсы валют -----------------
    static async Task<string> GetRates()
    {
        try
        {
            var json = JObject.Parse(await http.GetStringAsync("https://api.exchangerate-api.com/v4/latest/USD"));
            var usdToRub = json["rates"]["RUB"]?.Value<decimal>() ?? 0;
            var usdToKzt = json["rates"]["KZT"]?.Value<decimal>() ?? 0;
            var rubToKzt = usdToKzt / usdToRub;
            return $"💵 Курсы валют:\n" +
                   $"$1 = {usdToRub:F2} ₽\n" +
                   $"$1 = {usdToKzt:F2} ₸\n" +
                   $"1 ₽ = {rubToKzt:F2} ₸";
        }
        catch { return "❌ Не удалось загрузить курсы."; }
    }

    static Task HandleError(ITelegramBotClient botClient, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        Console.WriteLine($"Ошибка: {ex.Message}");
        return Task.CompletedTask;
    }
}
