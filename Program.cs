using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public class DebtRecord
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public string? FullName { get; set; }
    public string? Group { get; set; }
    public string? Subject { get; set; }
    public string? TaskDescription { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime RecordDate { get; set; }
    public DateTime DueDate { get; set; }
}

public class Admin
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AppDbContext : DbContext
{
    public DbSet<Admin> Admins { get; set; } = null!;
    public DbSet<DebtRecord> DebtRecords { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite("Data Source=debts.db");

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DebtRecord>().HasKey(d => d.Id);
        modelBuilder.Entity<Admin>().HasKey(a => a.Id);
        modelBuilder.Entity<Admin>().HasIndex(a => a.UserId).IsUnique();
    }
}

public class BotManager
{
    private static TelegramBotClient _botClient = null!;
    private const string BotToken = "7662972033:AAE4jd8nALzml3DYVwPdAPravLY4mXxtQk8";
    
    private static readonly Dictionary<long, string> UserStates = new();
    private static readonly Dictionary<long, DebtRecord> TempRecords = new();

    public static async Task Main()
    {
        var contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=debts.db")
            .Options;

        using (var context = new AppDbContext(contextOptions))
        {
            await context.Database.MigrateAsync();
            await EnsureAdminExists(context);
        }

        _botClient = new TelegramBotClient(BotToken);
        
        try
        {
            await _botClient.DeleteWebhookAsync(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Webhook error: {ex.Message}");
        }

        var receiverOptions = new ReceiverOptions 
        { 
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        _botClient.StartReceiving(
            HandleUpdateAsync,
            HandlePollingErrorAsync,
            receiverOptions
        );

        Console.WriteLine("Bot started!");
        Console.ReadLine();
    }

    private static async Task EnsureAdminExists(AppDbContext context)
    {
        if (!await context.Admins.AnyAsync())
        {
            context.Admins.Add(new Admin { UserId = 6426468905 });
            await context.SaveChangesAsync();
        }
    }

    private static ReplyKeyboardMarkup GetMainMenu(long userId)
    {
        var isAdmin = IsAdmin(userId).Result;
        var buttons = new List<KeyboardButton[]>
        {
            new[] { new KeyboardButton("📝 Новая запись"), new KeyboardButton("📋 Мои записи") }
        };

        if(isAdmin)
        {
            buttons.Add(new[] { new KeyboardButton("👑 Все записи"), new KeyboardButton("👥 Управление админами") });
        }

        buttons.Add(new[] { new KeyboardButton("ℹ️ Помощь") });

        return new ReplyKeyboardMarkup(buttons) { ResizeKeyboard = true };
    }

    private static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Error: {exception.Message}");
        return Task.CompletedTask;
    }

    private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            var handler = update switch
            {
                { Message: { } message } => HandleMessageAsync(botClient, message, cancellationToken),
                { CallbackQuery: { } callbackQuery } => HandleCallbackQueryAsync(botClient, callbackQuery, cancellationToken),
                _ => Task.CompletedTask
            };

            await handler;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update error: {ex.Message}");
        }
    }

    private static async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        if (message.Text == null || message.From == null) return;

        var chatId = message.Chat.Id;
        var userId = message.From.Id;
        var text = message.Text.Trim();
        var isAdmin = await IsAdmin(userId);

        if (text == "/start" || text == "❌ Отмена")
        {
            await botClient.SendTextMessageAsync(
                chatId,
                "Главное меню:",
                replyMarkup: GetMainMenu(userId),
                cancellationToken: cancellationToken);
            return;
        }

        if (UserStates.ContainsKey(userId))
        {
            await HandleStepInput(botClient, message, cancellationToken);
            return;
        }

        switch (text)
        {
            case "📝 Новая запись":
                await StartNewRecordProcess(botClient, chatId, userId, cancellationToken);
                break;
            
            case "📋 Мои записи":
                await ShowUserRecords(botClient, chatId, userId, cancellationToken);
                break;
            
            case "👑 Все записи" when isAdmin:
                await ShowAllRecords(botClient, chatId, cancellationToken);
                break;
            
            case "👥 Управление админами" when isAdmin:
                await ShowAdminManagement(botClient, chatId, cancellationToken);
                break;
            
            case "ℹ️ Помощь":
                await ShowHelp(botClient, chatId, isAdmin, cancellationToken);
                break;
        }
    }

    private static async Task StartNewRecordProcess(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        UserStates[userId] = "waiting_fullname";
        TempRecords[userId] = new DebtRecord { UserId = userId };
        
        await botClient.SendTextMessageAsync(
            chatId,
            "👤 Введите ваше ФИО полностью:",
            replyMarkup: new ReplyKeyboardMarkup(new[] { new KeyboardButton("❌ Отмена") }) { ResizeKeyboard = true },
            cancellationToken: cancellationToken);
    }

    private static async Task HandleStepInput(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        if (message.From == null || message.Text == null) return;

        var userId = message.From.Id;
        var chatId = message.Chat.Id;
        var text = message.Text.Trim();

        if (text == "❌ Отмена")
        {
            await CancelCurrentAction(botClient, chatId, userId, cancellationToken);
            return;
        }

        try
        {
            switch (UserStates[userId])
            {
                case "waiting_fullname": 
                    if (text.Length < 5 || text.Length > 100)
                    {
                        await botClient.SendTextMessageAsync(
                            chatId,
                            "❌ ФИО должно содержать от 5 до 100 символов",
                            cancellationToken: cancellationToken);
                        return;
                    }

                    TempRecords[userId].FullName = text;
                    UserStates[userId] = "waiting_group";
                    await botClient.SendTextMessageAsync(
                        chatId,
                        "📚 Введите вашу группу:",
                        cancellationToken: cancellationToken);
                    break;

                case "waiting_group":
                    TempRecords[userId].Group = text;
                    UserStates[userId] = "waiting_subject";
                    await botClient.SendTextMessageAsync(
                        chatId,
                        "📖 Введите предмет:",
                        cancellationToken: cancellationToken);
                    break;

                case "waiting_subject":
                    TempRecords[userId].Subject = text;
                    UserStates[userId] = "waiting_task";
                    await botClient.SendTextMessageAsync(
                        chatId,
                        "📝 Введите задание:",
                        cancellationToken: cancellationToken);
                    break;

                case "waiting_task":
                    TempRecords[userId].TaskDescription = text;
                    UserStates[userId] = "waiting_date";
                    await botClient.SendTextMessageAsync(
                        chatId,
                        $"📅 Введите дату и время сдачи (ДД.ММ.ГГГГ ЧЧ:мм)\nПример: {DateTime.Now.AddDays(3):dd.MM.yyyy HH:mm}",
                        cancellationToken: cancellationToken);
                    break;

                case "waiting_date":
                    if (DateTime.TryParseExact(text, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dueDate))
                    {
                        if (dueDate < DateTime.Now)
                        {
                            await botClient.SendTextMessageAsync(
                                chatId,
                                "❌ Дата должна быть в будущем!",
                                cancellationToken: cancellationToken);
                            return;
                        }

                        TempRecords[userId].DueDate = dueDate;
                        
                        var record = TempRecords[userId];
                        var confirmMessage = $"Подтвердите запись:\n\n" +
                                            $"👤 ФИО: {record.FullName ?? "Не указано"}\n" +
                                            $"📚 Группа: {record.Group ?? "Не указана"}\n" +
                                            $"📖 Предмет: {record.Subject ?? "Не указан"}\n" +
                                            $"📝 Задание: {record.TaskDescription ?? "Не указано"}\n" +
                                            $"📅 Срок: {dueDate:dd.MM.yyyy HH:mm}";

                        await botClient.SendTextMessageAsync(
                            chatId,
                            confirmMessage,
                            replyMarkup: new InlineKeyboardMarkup(new[]
                            {
                                new[] { InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "confirm") }
                            }),
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(
                            chatId,
                            $"❌ Неверный формат! Используйте ДД.ММ.ГГГГ ЧЧ:мм\nПример: {DateTime.Now:dd.MM.yyyy HH:mm}",
                            cancellationToken: cancellationToken);
                    }
                    break;

                case "adding_admin":
                    if (long.TryParse(text, out var newAdminId))
                    {
                        await using (var db = CreateDbContext())
                        {
                            if (!await db.Admins.AnyAsync(a => a.UserId == newAdminId))
                            {
                                db.Admins.Add(new Admin { UserId = newAdminId });
                                await db.SaveChangesAsync(cancellationToken);
                                await botClient.SendTextMessageAsync(
                                    chatId,
                                    $"✅ Пользователь {newAdminId} добавлен в админы",
                                    replyMarkup: GetMainMenu(userId),
                                    cancellationToken: cancellationToken);
                            }
                            else
                            {
                                await botClient.SendTextMessageAsync(
                                    chatId,
                                    $"❌ Пользователь {newAdminId} уже является администратором",
                                    replyMarkup: GetMainMenu(userId),
                                    cancellationToken: cancellationToken);
                            }
                        }
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(
                            chatId,
                            "❌ Неверный формат ID. Введите числовой идентификатор",
                            replyMarkup: GetMainMenu(userId),
                            cancellationToken: cancellationToken);
                    }
                    UserStates.Remove(userId);
                    break;

                case "removing_admin":
                    if (long.TryParse(text, out var removeAdminId))
                    {
                        await using (var db = CreateDbContext())
                        {
                            var admin = await db.Admins
                                .FirstOrDefaultAsync(a => a.UserId == removeAdminId);
                            
                            if (admin != null)
                            {
                                db.Admins.Remove(admin);
                                await db.SaveChangesAsync(cancellationToken);
                                await botClient.SendTextMessageAsync(
                                    chatId,
                                    $"✅ Администратор {removeAdminId} удалён",
                                    replyMarkup: GetMainMenu(userId),
                                    cancellationToken: cancellationToken);
                            }
                            else
                            {
                                await botClient.SendTextMessageAsync(
                                    chatId,
                                    $"❌ Администратор {removeAdminId} не найден",
                                    replyMarkup: GetMainMenu(userId),
                                    cancellationToken: cancellationToken);
                            }
                        }
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(
                            chatId,
                            "❌ Неверный формат ID. Введите числовой идентификатор",
                            replyMarkup: GetMainMenu(userId),
                            cancellationToken: cancellationToken);
                    }
                    UserStates.Remove(userId);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Input error: {ex.Message}");
            await botClient.SendTextMessageAsync(
                chatId,
                "❌ Произошла ошибка при обработке запроса",
                replyMarkup: GetMainMenu(userId),
                cancellationToken: cancellationToken);
            UserStates.Remove(userId);
        }
    }

    private static async Task CancelCurrentAction(
        ITelegramBotClient botClient,
        long chatId,
        long userId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (UserStates.ContainsKey(userId))
            {
                UserStates.Remove(userId);
                TempRecords.Remove(userId);
            }

            await botClient.SendTextMessageAsync(
                chatId,
                "❌ Действие отменено",
                replyMarkup: GetMainMenu(userId),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cancel error: {ex.Message}");
        }
    }

    private static async Task ShowAdminManagement(
        ITelegramBotClient botClient,
        long chatId,
        CancellationToken cancellationToken)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("➕ Добавить админа", "add_admin"),
                InlineKeyboardButton.WithCallbackData("➖ Удалить админа", "remove_admin")
            },
        });

        await botClient.SendTextMessageAsync(
            chatId,
            "Управление администраторами:",
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }

    private static async Task HandleCallbackQueryAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        var data = callbackQuery.Data;
        var chatId = callbackQuery.Message!.Chat.Id;
        var messageId = callbackQuery.Message.MessageId;
        var userId = callbackQuery.From.Id;

        try
        {
            if (data.StartsWith("toggle_"))
            {
                var recordId = int.Parse(data.Split('_')[1]);
                await using var db = CreateDbContext();
                var record = await db.DebtRecords.FindAsync(recordId);
                
                if (record == null)
                {
                    await botClient.AnswerCallbackQueryAsync(
                        callbackQuery.Id,
                        "❌ Запись не найдена",
                        cancellationToken: cancellationToken);
                    return;
                }

                record.IsCompleted = !record.IsCompleted;
                await db.SaveChangesAsync(cancellationToken);
                
                await UpdateRecordMessage(botClient, chatId, messageId, record, cancellationToken);
                await botClient.AnswerCallbackQueryAsync(
                    callbackQuery.Id,
                    $"Статус изменён на {(record.IsCompleted ? "Выполнено" : "Не выполнено")}",
                    cancellationToken: cancellationToken);
            }
            else if (data.StartsWith("delete_"))
            {
                var recordId = int.Parse(data.Split('_')[1]);
                await using var db = CreateDbContext();
                var record = await db.DebtRecords.FindAsync(recordId);
                
                if (record == null)
                {
                    await botClient.AnswerCallbackQueryAsync(
                        callbackQuery.Id,
                        "❌ Запись не найдена",
                        cancellationToken: cancellationToken);
                    return;
                }

                db.DebtRecords.Remove(record);
                await db.SaveChangesAsync(cancellationToken);
                
                await botClient.DeleteMessageAsync(chatId, messageId, cancellationToken);
                await botClient.AnswerCallbackQueryAsync(
                    callbackQuery.Id,
                    "✅ Запись удалена!",
                    cancellationToken: cancellationToken);
            }
            else
            {
                switch (data)
                {
                    case "confirm":
                        await using (var db = CreateDbContext())
                        {
                            db.DebtRecords.Add(TempRecords[userId]);
                            await db.SaveChangesAsync(cancellationToken);
                        }
                        TempRecords.Remove(userId);
                        UserStates.Remove(userId);
                        await botClient.SendTextMessageAsync(
                            chatId,
                            "✅ Запись успешно сохранена!",
                            replyMarkup: GetMainMenu(userId),
                            cancellationToken: cancellationToken);
                        break;

                    case "add_admin":
                        await botClient.SendTextMessageAsync(
                            chatId,
                            "Введите ID пользователя для добавления в админы:",
                            cancellationToken: cancellationToken);
                        UserStates[userId] = "adding_admin";
                        break;

                    case "remove_admin":
                        await botClient.SendTextMessageAsync(
                            chatId,
                            "Введите ID администратора для удаления:",
                            cancellationToken: cancellationToken);
                        UserStates[userId] = "removing_admin";
                        break;

                    case "list_admins":
                        await ShowAllAdmins(botClient, chatId, cancellationToken);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Callback error: {ex.Message}");
            await botClient.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                "❌ Произошла ошибка при обработке запроса",
                cancellationToken: cancellationToken);
        }
    }

    private static async Task UpdateRecordMessage(
        ITelegramBotClient botClient,
        long chatId,
        int messageId,
        DebtRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = record.IsCompleted ? "✅ Выполнено" : "🕒 В процессе";
            var text = $"📌 Запись #{record.Id}\n" +
                       $"👤 Студент: {record.FullName ?? "Не указано"} (ID: {record.UserId})\n" +
                       $"📚 Группа: {record.Group ?? "Не указана"}\n" +
                       $"📖 Предмет: {record.Subject ?? "Не указан"}\n" +
                       $"📝 Задание: {record.TaskDescription ?? "Не указано"}\n" +
                       $"📅 Срок: {record.DueDate:dd.MM.yyyy HH:mm}\n" +
                       $"🏷 Статус: {status}";

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        record.IsCompleted ? "❌ Отметить невыполненным" : "✅ Выполнить", 
                        $"toggle_{record.Id}"),
                    InlineKeyboardButton.WithCallbackData("🗑 Удалить", $"delete_{record.Id}")
                }
            });

            await botClient.EditMessageTextAsync(
                chatId,
                messageId,
                text,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update message error: {ex.Message}");
        }
    }

    private static async Task ShowUserRecords(
        ITelegramBotClient botClient,
        long chatId,
        long userId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = CreateDbContext();
            var records = await db.DebtRecords
                .Where(r => r.UserId == userId)
                .OrderBy(r => r.DueDate)
                .ToListAsync(cancellationToken);

            if (!records.Any())
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "📭 У вас нет активных записей.",
                    replyMarkup: GetMainMenu(userId),
                    cancellationToken: cancellationToken);
                return;
            }

            foreach (var record in records)
            {
                var status = record.IsCompleted ? "✅ Выполнено" : "🕒 В процессе";
                var text = $"📌 Запись #{record.Id}\n" +
                           $"👤 ФИО: {record.FullName ?? "Не указано"}\n" +
                           $"📚 Группа: {record.Group ?? "Не указана"}\n" +
                           $"📖 Предмет: {record.Subject ?? "Не указан"}\n" +
                           $"📝 Задание: {record.TaskDescription ?? "Не указано"}\n" +
                           $"📅 Срок: {record.DueDate:dd.MM.yyyy HH:mm}\n" +
                           $"🏷 Статус: {status}";

                await botClient.SendTextMessageAsync(
                    chatId,
                    text,
                    replyMarkup: GetMainMenu(userId),
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка показа записей: {ex.Message}");
        }
    }

    private static async Task ShowAllRecords(
        ITelegramBotClient botClient,
        long chatId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = CreateDbContext();
            var records = await db.DebtRecords
                .OrderBy(r => r.DueDate)
                .ToListAsync(cancellationToken);

            if (!records.Any())
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "📭 Нет активных записей.",
                    replyMarkup: GetMainMenu(chatId),
                    cancellationToken: cancellationToken);
                return;
            }

            foreach (var record in records)
            {
                var status = record.IsCompleted ? "✅ Выполнено" : "🕒 В процессе";
                var text = $"📌 Запись #{record.Id}\n" +
                           $"👤 Студент: {record.FullName ?? "Не указано"} (ID: {record.UserId})\n" +
                           $"📚 Группа: {record.Group ?? "Не указана"}\n" +
                           $"📖 Предмет: {record.Subject ?? "Не указан"}\n" +
                           $"📝 Задание: {record.TaskDescription ?? "Не указано"}\n" +
                           $"📅 Срок: {record.DueDate:dd.MM.yyyy HH:mm}\n" +
                           $"🏷 Статус: {status}";

                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData(
                            record.IsCompleted ? "❌ Отметить невыполненным" : "✅ Выполнить", 
                            $"toggle_{record.Id}"),
                        InlineKeyboardButton.WithCallbackData("🗑 Удалить", $"delete_{record.Id}")
                    }
                });

                await botClient.SendTextMessageAsync(
                    chatId,
                    text,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка показа всех записей: {ex.Message}");
        }
    }

    private static async Task ShowAllAdmins(
        ITelegramBotClient botClient,
        long chatId,
        CancellationToken cancellationToken)
    {
        await using var db = CreateDbContext();
        var admins = await db.Admins.ToListAsync(cancellationToken);

        var adminList = admins.Any() 
            ? "👑 Список администраторов:\n" + string.Join("\n", admins.Select(a => $"• ID: {a.UserId}")) 
            : "❌ Список администраторов пуст";

        await botClient.SendTextMessageAsync(
            chatId,
            adminList,
            cancellationToken: cancellationToken);
    }

    private static async Task ShowHelp(
        ITelegramBotClient botClient,
        long chatId,
        bool isAdmin,
        CancellationToken cancellationToken)
    {
        var helpText = "📌 Основные команды:\n" +
                      "📝 Новая запись - Создать новую запись\n" +
                      "📋 Мои записи - Показать мои записи\n";

        if (isAdmin)
        {
            helpText += "\n\n👑 Команды администратора:\n" +
                       "👑 Все записи - Показать все записи\n" +
                       "👥 Управление админами - Управление правами администраторов";
        }

        await botClient.SendTextMessageAsync(
            chatId,
            helpText,
            replyMarkup: GetMainMenu(chatId),
            cancellationToken: cancellationToken);
    }

    private static async Task<bool> IsAdmin(long userId)
    {
        await using var db = CreateDbContext();
        return await db.Admins.AnyAsync(a => a.UserId == userId);
    }

    private static AppDbContext CreateDbContext()
    {
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=debts.db")
            .Options);
    }
}