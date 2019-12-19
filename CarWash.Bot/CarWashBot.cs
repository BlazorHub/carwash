// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CarWash.Bot.CognitiveModels;
using CarWash.Bot.Dialogs;
using CarWash.Bot.States;
using CarWash.ClassLibrary.Enums;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.AI.Luis;
using Microsoft.Bot.Builder.AI.QnA;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Configuration;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Recognizers.Text.DataTypes.TimexExpression;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;

namespace CarWash.Bot
{
    /// <summary>
    /// Main entry point and orchestration for bot.
    /// </summary>
    public class CarWashBot : IBot
    {
        /// <summary>
        /// Key in the bot config (.bot file) for the Endpoint.
        /// </summary>
        public const string EndpointConfiguration = "production";

        /// <summary>
        /// Key in the bot config (.bot file) for the DEV Endpoint.
        /// </summary>
        public const string EndpointConfigurationDev = "development";

        /// <summary>
        /// Key in the bot config (.bot file) for the Storage instance.
        /// </summary>
        public const string StorageConfiguration = "carwashstorage";

        /// <summary>
        /// Storage Table name of the user storage for proactive messaging.
        /// </summary>
        public const string UserStorageTableName = "ProactiveBotUsers";

        /// <summary>
        /// Key in the bot config (.bot file) for the LUIS instance.
        /// </summary>
        public const string LuisConfiguration = "carwashubot";

        /// <summary>
        /// Key in the bot config (.bot file) for the QnA Maker instance.
        /// </summary>
        public const string QnAMakerConfiguration = "carwashufaq";

        /// <summary>
        /// Key in the bot config (.bot file) for Service Bus.
        /// </summary>
        public const string ServiceBusConfiguration = "carwashuservicebus";

        // Supported LUIS Intents
        private const string NewReservationIntent = "Reservation_Add";
        private const string CancelReservationIntent = "Reservation_Delete";
        private const string EditReservationIntent = "Reservation_Edit";
        private const string FindReservationIntent = "Reservation_Find";
        private const string NextFreeSlotIntent = "Reservation_NextFreeSlot";
        private const string StopIntent = "Stop";
        private const string HelpIntent = "Help";
        private const string NoneIntent = "None";
        private const string WeatherIntent = "Weather_GetForecast";

        // Card actions
        private const string DropoffAction = "dropoff";
        private const string CancelAction = "cancel";

        private readonly StateAccessors _accessors;
        private readonly LuisRecognizer _luis;
        private readonly QnAMaker _qna;
        private readonly CloudStorageAccount _storage;
        private readonly TelemetryClient _telemetryClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="CarWashBot"/> class.
        /// </summary>
        /// <param name="accessors">The state accessors for managing bot state.</param>
        /// <param name="botConfig">The parsed .bot config file.</param>
        /// <param name="services">External services.</param>
        /// <param name="loggerFactory">Logger.</param>
        /// <param name="telemetryClient">Telemetry client.</param>
        public CarWashBot(StateAccessors accessors, BotConfiguration botConfig, BotServices services, ILoggerFactory loggerFactory, TelemetryClient telemetryClient)
        {
            _accessors = accessors ?? throw new ArgumentNullException(nameof(accessors));
            if (botConfig == null) throw new ArgumentNullException(nameof(botConfig));
            if (services == null) throw new ArgumentNullException(nameof(services));

            _telemetryClient = telemetryClient;

            // Verify LUIS configuration.
            if (!services.LuisServices.ContainsKey(LuisConfiguration))
                throw new InvalidOperationException($"Invalid configuration. Please check your '.bot' file for a LUIS service named '{LuisConfiguration}'.");
            _luis = services.LuisServices[LuisConfiguration];

            // Verify QnAMaker configuration.
            if (!services.QnAServices.ContainsKey(QnAMakerConfiguration))
                throw new ArgumentException($"Invalid configuration. Please check your '.bot' file for a QnA service named '{QnAMakerConfiguration}'.");
            _qna = services.QnAServices[QnAMakerConfiguration];

            // Verify Storage configuration.
            if (!services.StorageServices.ContainsKey(StorageConfiguration))
                throw new ArgumentException($"Invalid configuration. Please check your '.bot' file for a Storage service named '{StorageConfiguration}'.");
            _storage = services.StorageServices[StorageConfiguration];
            _tableStorage = services.TableServices[StorageConfiguration];

            Dialogs = new DialogSet(_accessors.DialogStateAccessor);
            Dialogs.Add(new NewReservationDialog(_accessors.NewReservationStateAccessor, telemetryClient));
            Dialogs.Add(new ConfirmDropoffDialog(_accessors.ConfirmDropoffStateAccessor, telemetryClient));
            Dialogs.Add(new CancelReservationDialog(_accessors.CancelReservationStateAccessor, telemetryClient));
            Dialogs.Add(new FindReservationDialog(telemetryClient));
            Dialogs.Add(new NextFreeSlotDialog(telemetryClient));
            Dialogs.Add(new AuthDialog(accessors.UserProfileAccessor, _tableStorage, telemetryClient));
            Dialogs.Add(AuthDialog.LoginPromptDialog());

            // Dialogs.Add(FormDialog.FromForm(NewReservationForm.BuildForm));
        }

        private DialogSet Dialogs { get; set; }

        /// <summary>
        /// Run every turn of the conversation. Handles orchestration of messages.
        /// </summary>
        /// <param name="turnContext">Bot Turn Context.</param>
        /// <param name="cancellationToken">Task CancellationToken.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            var activity = turnContext.Activity;

            // Create a dialog context
            var dc = await Dialogs.CreateContextAsync(turnContext, cancellationToken);

            switch (activity.Type)
            {
                case ActivityTypes.Message:
                    {
                        // This bot is not case sensitive.
                        var text = turnContext.Activity.Text?.Trim()?.ToLowerInvariant();

                        if (text == null)
                        {
                            string action = ((dynamic)turnContext.Activity.Value)?.action;
                            string id = ((dynamic)turnContext.Activity.Value)?.id;

                            switch (action)
                            {
                                case DropoffAction:
                                    await dc.BeginDialogAsync(
                                        nameof(ConfirmDropoffDialog),
                                        new ConfirmDropoffDialog.ConfirmDropoffDialogOptions { ReservationId = id },
                                        cancellationToken: cancellationToken);
                                    break;
                                case CancelAction:
                                    await dc.BeginDialogAsync(
                                        nameof(CancelReservationDialog),
                                        new CancelReservationDialog.CancelReservationDialogOptions { ReservationId = id },
                                        cancellationToken: cancellationToken);
                                    break;
                            }

                            // Continue the current dialog
                            await dc.ContinueDialogAsync(cancellationToken);
                            break;
                        }

                        if (text == "help")
                        {
                            await SendWelcomeMessageAsync(turnContext, cancellationToken);
                            break;
                        }

                        if (text == "login")
                        {
                            // Start the Login process.
                            await dc.BeginDialogAsync(nameof(AuthDialog), cancellationToken: cancellationToken);
                            break;
                        }

                        if (text == "logout")
                        {
                            // The bot adapter encapsulates the authentication processes.
                            var botAdapter = (BotFrameworkAdapter)turnContext.Adapter;
                            await botAdapter.SignOutUserAsync(turnContext, AuthDialog.AuthConnectionName, cancellationToken: cancellationToken);
                            await turnContext.SendActivityAsync("You have been signed out.", cancellationToken: cancellationToken);
                            break;
                        }

                        if (text == "!generatetranscripts") await GenerateTranscriptsAsync(turnContext);

                        // Perform a call to LUIS to retrieve results for the current activity message.
                        var luisResults = await _luis.RecognizeAsync(dc.Context, cancellationToken).ConfigureAwait(false);

                        var (intent, score) = luisResults.GetTopScoringIntent();
                        var entities = ParseLuisForEntities(luisResults);

                        // Log LUIS to AppInsights
                        LogLuis(luisResults, intent, score, entities);

                        // Handle conversation interrupts first.
                        var interrupted = await IsTurnInterruptedAsync(dc, intent);
                        if (interrupted)
                        {
                            // Bypass the dialog.
                            // Save state before the next turn.
                            await _accessors.ConversationState.SaveChangesAsync(turnContext, cancellationToken: cancellationToken);
                            await _accessors.UserState.SaveChangesAsync(turnContext, cancellationToken: cancellationToken);
                            return;
                        }

                        // Continue the current dialog
                        var dialogResult = await dc.ContinueDialogAsync(cancellationToken);

                        // if no one has responded,
                        if (!dc.Context.Responded)
                        {
                            // examine results from active dialog
                            switch (dialogResult.Status)
                            {
                                case DialogTurnStatus.Empty:
                                    switch (intent)
                                    {
                                        case NewReservationIntent:
                                            await dc.BeginDialogAsync(
                                                nameof(NewReservationDialog),
                                                new NewReservationDialog.NewReservationDialogOptions { LuisEntities = entities },
                                                cancellationToken);

                                            // await dc.BeginDialogAsync(
                                            //    nameof(NewReservationForm),
                                            //    cancellationToken);
                                            break;

                                        case EditReservationIntent:
                                            var reply = turnContext.Activity.CreateReply();
                                            reply.Attachments.Add(new HeroCard
                                            {
                                                Text = "Please open the app to make modifications to your reservations.",
                                                Buttons = new List<CardAction> { new CardAction(ActionTypes.OpenUrl, "Open the app", value: "https://carwashu.azurewebsites.net/") },
                                            }.ToAttachment());
                                            await turnContext.SendActivityAsync(reply, cancellationToken);
                                            break;

                                        case CancelReservationIntent:
                                            await dc.BeginDialogAsync(
                                                nameof(CancelReservationDialog),
                                                cancellationToken: cancellationToken);
                                            break;

                                        case FindReservationIntent:
                                            await dc.BeginDialogAsync(nameof(FindReservationDialog), cancellationToken: cancellationToken);
                                            break;

                                        case NextFreeSlotIntent:
                                            await dc.BeginDialogAsync(nameof(NextFreeSlotDialog), cancellationToken: cancellationToken);
                                            break;

                                        case WeatherIntent:
                                            await turnContext.SendActivityAsync("They say, I'll get access to the internet soon, and will be able to answer what the weather will look like...", cancellationToken: cancellationToken);
                                            break;

                                        case NoneIntent:
                                            // Check QnA Maker model
                                            var response = await _qna.GetAnswersAsync(turnContext);
                                            if (response != null && response.Length > 0)
                                            {
                                                await turnContext.SendActivityAsync(response[0].Answer, cancellationToken: cancellationToken);
                                            }
                                            else
                                            {
                                                _telemetryClient.TrackEvent(
                                                    "Understanding user failed",
                                                    new Dictionary<string, string>
                                                    {
                                                    { "Event Name", "Understanding user failed" },
                                                    { "Activity Type", turnContext.Activity.Type },
                                                    { "Message", text },
                                                    { "Channel ID", turnContext.Activity.ChannelId },
                                                    { "Activity ID", turnContext.Activity.Id },
                                                    { "Conversation ID", turnContext.Activity.Conversation.Id },
                                                    { "From ID", turnContext.Activity.From.Id },
                                                    { "Recipient ID", turnContext.Activity.Recipient.Id },
                                                    { "Value", JsonConvert.SerializeObject(turnContext.Activity.Value) },
                                                    { "LUIS intent 'None'", "true" },
                                                    });

                                                await dc.Context.SendActivityAsync("I didn't understand what you just said to me.", cancellationToken: cancellationToken);
                                            }

                                            break;

                                        default:
                                            _telemetryClient.TrackEvent(
                                                "Understanding user failed",
                                                new Dictionary<string, string>
                                                {
                                                    { "Event Name", "Understanding user failed" },
                                                    { "Activity Type", turnContext.Activity.Type },
                                                    { "Message", text },
                                                    { "Channel ID", turnContext.Activity.ChannelId },
                                                    { "Activity ID", turnContext.Activity.Id },
                                                    { "Conversation ID", turnContext.Activity.Conversation.Id },
                                                    { "From ID", turnContext.Activity.From.Id },
                                                    { "Recipient ID", turnContext.Activity.Recipient.Id },
                                                    { "Value", JsonConvert.SerializeObject(turnContext.Activity.Value) },
                                                    { "LUIS intent 'None'", "false" },
                                                });

                                            // Help or no intent identified, either way, let's provide some help to the user
                                            await dc.Context.SendActivityAsync("I didn't understand what you just said to me.", cancellationToken: cancellationToken);
                                            break;
                                    }

                                    break;

                                case DialogTurnStatus.Waiting:
                                    // The active dialog is waiting for a response from the user, so do nothing.
                                    break;

                                case DialogTurnStatus.Complete:
                                    await dc.EndDialogAsync(cancellationToken: cancellationToken);
                                    break;

                                default:
                                    await dc.CancelAllDialogsAsync(cancellationToken);
                                    break;
                            }
                        }

                        break;
                    }

                case ActivityTypes.Event:
                case ActivityTypes.Invoke:
                    // This handles the MS Teams Invoke Activity sent when magic code is not used.
                    // See: https://docs.microsoft.com/en-us/microsoftteams/platform/concepts/authentication/auth-oauth-card#getting-started-with-oauthcard-in-teams
                    // The Teams manifest schema is found here: https://docs.microsoft.com/en-us/microsoftteams/platform/resources/schema/manifest-schema
                    // It also handles the Event Activity sent from the emulator when the magic code is not used.
                    // See: https://blog.botframework.com/2018/08/28/testing-authentication-to-your-bot-using-the-bot-framework-emulator/
                    // dc = await Dialogs.CreateContextAsync(turnContext, cancellationToken);
                    await dc.ContinueDialogAsync(cancellationToken);
                    if (!turnContext.Responded)
                    {
                        await dc.BeginDialogAsync(nameof(AuthDialog), cancellationToken: cancellationToken);
                    }

                    break;

                case ActivityTypes.ConversationUpdate:
                    {
                        if (activity.MembersAdded.Any())
                        {
                            // Iterate over all new members added to the conversation.
                            foreach (var member in activity.MembersAdded)
                            {
                                // Greet only the user and not the bot itself.
                                if (member.Id == activity.Recipient.Id) continue;
                                await SendWelcomeMessageAsync(turnContext, cancellationToken);
                                break;
                            }
                        }

                        break;
                    }
            }

            await _accessors.ConversationState.SaveChangesAsync(turnContext, cancellationToken: cancellationToken);
            await _accessors.UserState.SaveChangesAsync(turnContext, cancellationToken: cancellationToken);
        }

        // Determine if an interruption has occured before we dispatch to any active dialog.
        private static async Task<bool> IsTurnInterruptedAsync(DialogContext dc, string topIntent)
        {
            // See if there are any conversation interrupts we need to handle.
            if (topIntent.Equals(StopIntent))
            {
                if (dc.ActiveDialog != null)
                {
                    await dc.CancelAllDialogsAsync();
                    await dc.Context.SendActivityAsync("Sorry, I haven't noticed that the context changed. How can I help you?");
                }
                else
                {
                    await dc.Context.SendActivityAsync("How can I help you?");
                }

                return true;        // Handled the interrupt.
            }

            if (topIntent.Equals(HelpIntent))
            {
                await dc.Context.SendActivityAsync("Let me try to provide some help.");
                await dc.Context.SendActivityAsync("You can ask me about the state of your reservations, ask to make a new reservation for you or confirm the key drop-off.");
                if (dc.ActiveDialog != null)
                {
                    await dc.RepromptDialogAsync();
                }

                return true;        // Handled the interrupt.
            }

            return false;           // Did not handle the interrupt.
        }

        /// <summary>
        /// Send the welcome message.
        /// </summary>
        /// <param name="turnContext">Provides the <see cref="ITurnContext"/> for the turn of the bot.</param>
        /// <param name="cancellationToken" >(Optional) A <see cref="CancellationToken"/> that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A <see cref="Task"/> representing the operation result of the Turn operation.</returns>
        private async Task SendWelcomeMessageAsync(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            var profileState = await _accessors.UserProfileAccessor.GetAsync(turnContext, () => new UserProfile(), cancellationToken);
            if (profileState.IsWelcomeMessageSent) return;
            profileState.IsWelcomeMessageSent = true;
            await _accessors.UserProfileAccessor.SetAsync(turnContext, profileState);

            var greeting = turnContext.Activity.From?.Name == null ? "Hi!" : $"Hi {turnContext.Activity.From.Name}!";
            await turnContext.SendActivitiesAsync(
                new IActivity[]
                {
                    new Activity(type: ActivityTypes.Message, text: greeting),
                    new Activity(type: ActivityTypes.Message, text: "My name is C.I.C.A. (Cool and Intelligent Carwash Assistant) and I'm your bot 🤖 who will help you reserve car washing services and answer your questions."),
                    new Activity(type: ActivityTypes.Message, text: "Ask me questions like 'How to use the app?' or 'What does interior cleaning cost?'."),
                    new Activity
                    {
                        Type = ActivityTypes.Message,
                        InputHint = InputHints.AcceptingInput,
                        Text = "Or I can make reservations for you. But before that you need to log in by typing 'login'.",
                        SuggestedActions = new SuggestedActions
                        {
                            Actions = new List<CardAction>
                            {
                                new CardAction { Title = "login", Type = ActionTypes.ImBack, Value = "login" },
                                new CardAction { Title = "How to use the app?", Type = ActionTypes.ImBack, Value = "How to use the app?" },
                                new CardAction { Title = "What does interior cleaning cost?", Type = ActionTypes.ImBack, Value = "What does interior cleaning cost?" },
                            },
                        },
                    },
                },
                cancellationToken);
        }

        /// <summary>
        /// Generates .transcript files for consuming in Bot Framework Emulator.
        /// </summary>
        /// <param name="turnContext">Turn context.</param>
        private async Task GenerateTranscriptsAsync(ITurnContext turnContext)
        {
            var blobClient = _storage.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference("transcripts");

            // List channels (level 1 folders)
            var channels = new List<CloudBlobDirectory>();
            BlobContinuationToken continuationToken = null;
            do
            {
                var response = await container.ListBlobsSegmentedAsync(continuationToken);
                continuationToken = response.ContinuationToken;
                foreach (var directory in response.Results.OfType<CloudBlobDirectory>())
                {
                    channels.Add(directory);
                }
            }
            while (continuationToken != null);

            foreach (var channel in channels)
            {
                // List conversations (level 2 folders)
                var conversations = new List<CloudBlobDirectory>();
                continuationToken = null;
                do
                {
                    var response = await channel.ListBlobsSegmentedAsync(continuationToken);
                    continuationToken = response.ContinuationToken;
                    foreach (var directory in response.Results.OfType<CloudBlobDirectory>())
                    {
                        conversations.Add(directory);
                    }
                }
                while (continuationToken != null);

                foreach (var conversation in conversations)
                {
                    // List blobs on level 3
                    var activityBlobs = new List<CloudBlockBlob>();
                    continuationToken = null;
                    do
                    {
                        var response = await conversation.ListBlobsSegmentedAsync(continuationToken);
                        continuationToken = response.ContinuationToken;
                        foreach (var blob in response.Results.OfType<CloudBlockBlob>())
                        {
                            activityBlobs.Add(blob);
                        }
                    }
                    while (continuationToken != null);

                    // Order by timestamp
                    activityBlobs.OrderBy(a => DateTime.Parse(a.Metadata.FirstOrDefault(m => m.Key == "Timestamp").Value));

                    // Deserialize blobs to an Activity list
                    var activities = new List<Activity>();
                    foreach (var blob in activityBlobs)
                    {
                        var activityJson = await blob.DownloadTextAsync();
                        activities.Add(JsonConvert.DeserializeObject<Activity>(activityJson));

                        // Delete original blob
                        await blob.DeleteAsync();
                    }

                    var username = activities.FirstOrDefault(a => a.From.Name != null && a.From.Name != "CarWash")?.From.Name.Split('(')[0].Trim().Replace(' ', '_');

                    // Serialize and upload activity List
                    var transcriptBlob = container.GetBlockBlobReference($"{conversation.Parent.Prefix}{username}_{activities[0].Timestamp.Value.ToString("s")}.transcript");
                    await transcriptBlob.UploadTextAsync(JsonConvert.SerializeObject(activities, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                }
            }

            await turnContext.SendActivityAsync("Done");
        }

        /// <summary>
        /// Parses LUIS response to custom CognitiveModels.
        /// </summary>
        /// <param name="recognizerResult">LUIS response.</param>
        /// <returns>List of Entities (in the form of CognitiveModels).</returns>
        private List<CognitiveModel> ParseLuisForEntities(RecognizerResult recognizerResult)
        {
            /* Simple way 👇
             * var s = (string)recognizerResult.Entities["Service"]?[0];
             */

            var entities = new List<CognitiveModel>();

            try
            {
                var parsedLuisResponse = JsonConvert.DeserializeObject<LuisEntityResponse>(recognizerResult.Entities.ToString());

                foreach (var entityList in parsedLuisResponse.Instance)
                {
                    foreach (var entity in entityList.Value)
                    {
                        try
                        {
                            switch (entityList.Key)
                            {
                                case "Service":
                                    var service = new ServiceModel(entity);
                                    if (!Enum.TryParse(service.Text, true, out ServiceType serviceType)) continue;
                                    service.Service = serviceType;
                                    service.Type = LuisEntityType.Service;
                                    entities.Add(service);
                                    break;
                                case "datetime":
                                    var dateTime = new DateTimeModel(entity);
                                    string timex = ((dynamic)parsedLuisResponse.Entities["datetime"].ToList()[0]).timex[0];
                                    dateTime.Timex = new TimexProperty(timex);
                                    dateTime.Type = LuisEntityType.DateTime;
                                    entities.Add(dateTime);
                                    break;
                                case "Reservation_Comment":
                                    entity.Type = LuisEntityType.Comment;
                                    entities.Add(entity);
                                    break;
                                case "Reservation_Location_Building":
                                    entity.Type = LuisEntityType.Building;
                                    entities.Add(entity);
                                    break;
                                case "Reservation_Location_Floor":
                                    entity.Type = LuisEntityType.Floor;
                                    entities.Add(entity);
                                    break;
                                case "Reservation_Location_Seat":
                                    entity.Type = LuisEntityType.Seat;
                                    entities.Add(entity);
                                    break;
                                case "Reservation_Location_Private":
                                    entity.Type = LuisEntityType.Private;
                                    entities.Add(entity);
                                    break;
                                case "Reservation_Location_VehiclePlateNumber":
                                    entity.Type = LuisEntityType.VehiclePlateNumber;
                                    entities.Add(entity);
                                    break;
                                case "Weather_Location":
                                    entity.Type = LuisEntityType.WeatherLocation;
                                    entities.Add(entity);
                                    break;
                                default:
                                    break;
                            }
                        }
                        catch (Exception e)
                        {
                            _telemetryClient.TrackException(e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _telemetryClient.TrackException(e);
            }

            return entities;
        }

        /// <summary>
        /// Logs LUIS responses to Application Insights.
        /// </summary>
        /// <param name="luisResult">LUIS response.</param>
        /// <param name="intent">LUIS intent.</param>
        /// <param name="score">LUIS score of intent.</param>
        /// <param name="entities">List of parsed entities.</param>
        private void LogLuis(RecognizerResult luisResult, string intent, double score, List<CognitiveModel> entities)
        {
            // Collect information to send to Application Insights
            var logProperties = new Dictionary<string, string>
            {
                { "LUIS_query", luisResult.Text },
                { "LUIS_topScoringIntent", intent },
                { "LUIS_topScoringIntentScore", score.ToString() },
            };

            // Add entities to collected information
            int i = 1;
            foreach (var item in entities)
            {
                // Query: reserve [exterior] cleaning
                // item.Type = "Service"
                // item.Text = "exterior"
                logProperties.Add("LUIS_entities_" + i++ + "_" + item.Type, item.Text);
            }

            // Log LUIS to AppInsights
            _telemetryClient.TrackTrace("LUIS", SeverityLevel.Information, logProperties);
        }
    }
}
