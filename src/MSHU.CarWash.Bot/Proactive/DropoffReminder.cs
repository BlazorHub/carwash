﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azure.ServiceBus;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration;
using Microsoft.Bot.Configuration;
using Microsoft.Bot.Schema;
using MSHU.CarWash.ClassLibrary.Extensions;
using MSHU.CarWash.ClassLibrary.Models.ServiceBus;
using Newtonsoft.Json;

namespace MSHU.CarWash.Bot.Proactive
{
    public class DropoffReminder
    {
        /// <summary>
        /// Key in the bot config (.bot file) for the Endpoint.
        /// </summary>
        private const string EndpointConfiguration = "production";
        private const string EndpointConfigurationDev = "development";

        /// <summary>
        /// Key in the bot config (.bot file) for Service Bus.
        /// </summary>
        private const string ServiceBusConfiguration = "carwashuservicebus";

        /// <summary>
        /// Service Bus queue name.
        /// </summary>
        private const string ServiceBusQueueName = "bot-dropoff-reminder";
        private const string ServiceBusQueueNameDev = "bot-dropoff-reminder-dev";

        private readonly QueueClient _queueClient;
        private readonly EndpointService _endpoint;
        private readonly StateAccessors _accessors;
        private readonly BotFrameworkAdapter _botFrameworkAdapter;
        private readonly TelemetryClient _telemetryClient;

        public DropoffReminder(StateAccessors accessors, IAdapterIntegration adapterIntegration, IHostingEnvironment env, BotConfiguration botConfig, BotServices services)
        {
            _accessors = accessors;
            _botFrameworkAdapter = (BotFrameworkAdapter)adapterIntegration;
            _telemetryClient = new TelemetryClient();

            // Verify Endpoint configuration.
            var endpointConfig = env.IsProduction() ? EndpointConfiguration : EndpointConfigurationDev;
            if (!services.EndpointServices.ContainsKey(endpointConfig))
            {
                throw new ArgumentException($"Invalid configuration. Please check your '.bot' file for a Endpoint service named '{endpointConfig}'.");
            }

            _endpoint = services.EndpointServices[endpointConfig];

            // Verify ServiceBus configuration.
            if (!services.ServiceBusServices.ContainsKey(ServiceBusConfiguration))
            {
                throw new ArgumentException($"Invalid configuration. Please check your '.bot' file for a Service Bus service named '{ServiceBusConfiguration}'.");
            }

            var queueName = env.IsProduction() ? ServiceBusQueueName : ServiceBusQueueNameDev;
            _queueClient = new QueueClient(services.ServiceBusServices[ServiceBusConfiguration], queueName, ReceiveMode.PeekLock, null);
        }

        public void RegisterHandler()
        {
            // Configure the MessageHandler Options in terms of exception handling, number of concurrent messages to deliver etc.
            var messageHandlerOptions = new MessageHandlerOptions(ExceptionReceivedHandler)
            {
                // Maximum number of Concurrent calls to the callback `ProcessMessagesAsync`, set to 1 for simplicity.
                // Set it according to how many messages the application wants to process in parallel.
                MaxConcurrentCalls = 1,

                // Indicates whether MessagePump should automatically complete the messages after returning from User Callback.
                // False below indicates the Complete will be handled by the User Callback as in `ProcessMessagesAsync` below.
                AutoComplete = false,
            };

            // Register the function that will process messages
            _queueClient.RegisterMessageHandler(ProcessMessagesAsync, messageHandlerOptions);
        }

        private Task ExceptionReceivedHandler(ExceptionReceivedEventArgs args)
        {
            var context = args.ExceptionReceivedContext;
            _telemetryClient.TrackException(args.Exception, new Dictionary<string, string>
            {
                { "Endpoint", context.Endpoint },
                { "Entity Path", context.EntityPath },
                { "Executing Action", context.Action },
            });

            return Task.CompletedTask;
        }

        private async Task ProcessMessagesAsync(Message message, CancellationToken cancellationToken)
        {
            var json = Encoding.UTF8.GetString(message.Body);
            var reminder = JsonConvert.DeserializeObject<DropoffReminderMessage>(json);

            var user = new ChannelAccount("29:1Mvgp4Jo7JFW3u5phDbpjtN0HMjkHV2cPqqBu0pER4EftX6J0fAs2afCHpucbWmcHUByRoaHzKrWi6KTpSRVGlA", "Mark Szabo (Prohuman 2004 kft.)", RoleTypes.User);
            var bot = new ChannelAccount("28:3e58d71d-7fd2-4568-8a02-0f641a3dfbc5", "CarWash", RoleTypes.Bot);

            try
            {
                // TODO
                await _botFrameworkAdapter.CreateConversationAsync(
                    "msteams",
                    "https://smba.trafficmanager.net/amer/",
                    new Microsoft.Bot.Connector.Authentication.MicrosoftAppCredentials(_endpoint.AppId, _endpoint.AppPassword),
                    new ConversationParameters(bot: bot, members: new List<ChannelAccount> { user }, channelData: new { tenant = new { id = "72f988bf-86f1-41af-91ab-2d7cd011db47" } }),
                    DropOffReminderCallback(),
                    cancellationToken);

                // var conversation = new ConversationReference(
                //    null,
                //    user,
                //    bot,
                //    new ConversationAccount(null, null, "01b74930-086c-11e9-ad05-d9034388c99f|transcript", null, null, null),
                //    "msteams",
                //    "https://smba.trafficmanager.net/amer/");
                // await _botFrameworkAdapter.ContinueConversationAsync(_endpoint.AppId, conversation, DropOffReminderCallback(), cancellationToken);
            }
            catch (ErrorResponseException e)
            {
                _telemetryClient.TrackException(e, new Dictionary<string, string>
                {
                    { "Error message", e.Message },
                    { "Response body", e.Response.Content },
                    { "Request body", e.Request.Content },
                    { "Request uri", $"{e.Request.Method.ToString()} {e.Request.RequestUri.AbsoluteUri}" },
                    { "Request headers", e.Request.Headers.ToJson() },
                });
            }
            catch (Exception e)
            {
                _telemetryClient.TrackException(e);
            }

            // Note: Use the cancellationToken passed as necessary to determine if the queueClient has already been closed.
            // If queueClient has already been Closed, you may chose to not call CompleteAsync() or AbandonAsync() etc. calls
            // to avoid unnecessary exceptions.
            //
            // Complete the message so that it is not received again.
            // This can be done only if the queueClient is created in ReceiveMode.PeekLock mode (which is default).
            await _queueClient.CompleteAsync(message.SystemProperties.LockToken);

            _telemetryClient.TrackEvent(
                "Bot drop-off reminder proactive message sent.",
                new Dictionary<string, string>
                {
                    { "Reservation ID", reminder.ReservationId },
                    { "SequenceNumber", message.SystemProperties.SequenceNumber.ToString() },
                },
                new Dictionary<string, double>
                {
                    { "Proactive message", 1 },
                });
        }

        private BotCallbackHandler DropOffReminderCallback()
        {
            return async (turnContext, cancellationToken) =>
            {
                // Send the user a proactive reminder message.
                await turnContext.SendActivityAsync("It's time to leave the key at the reception and confirm vehicle location!");
            };
        }
    }
}
