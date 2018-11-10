﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Schema;
using MSHU.CarWash.Bot.Services;

namespace MSHU.CarWash.Bot.Dialogs.Auth
{
    /// <summary>
    /// User authentication.
    /// </summary>
    public class AuthDialog : ComponentDialog
    {
        /// <summary>
        /// The name of your connection. It can be found on Azure in
        /// your Bot Channels Registration on the settings blade.
        /// </summary>
        public const string AuthConnectionName = "adal";

        // Dialogs
        private const string Name = "auth";

        // Prompts
        private const string LoginPrompt = "loginPrompt";
        private const string DisplayTokenPrompt = "displayTokenPrompt";

        /// <summary>
        /// Initializes a new instance of the <see cref="AuthDialog"/> class.
        /// </summary>
        public AuthDialog() : base(nameof(AuthDialog))
        {
            var dialogSteps = new WaterfallStep[]
            {
                PromptStepAsync,
                LoginStepAsync,
            };

            // Add the OAuth prompts and related dialogs into the dialog set
            AddDialog(new WaterfallDialog(Name, dialogSteps));
            AddDialog(LoginPromptDialog());
            //AddDialog(new ConfirmPrompt(DisplayTokenPrompt));
            //AddDialog(new WaterfallDialog(Name, new WaterfallStep[] { PromptStepAsync, LoginStepAsync, DisplayTokenAsync }));
        }

        /// <summary>
        /// Get a token from prompt.
        /// </summary>
        /// <param name="dc">Dialog context.</param>
        /// <param name="cancellationToken" >(Optional) A <see cref="CancellationToken"/> that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>token string</returns>
        public static async Task<string> GetToken(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Prompts the user to login using the OAuth provider specified by the connection name.
            var prompt = await dc.BeginDialogAsync(LoginPrompt, cancellationToken: cancellationToken);
            var tokenResponse = (TokenResponse)prompt.Result;

            return tokenResponse?.Token;
        }

        /// <summary>
        /// Prompts the user to login using the OAuth provider specified by the connection name.
        /// </summary>
        /// <param name="connectionName"> The name of your connection. It can be found on Azure in
        /// your Bot Channels Registration on the settings blade. </param>
        /// <returns> An <see cref="OAuthPrompt"/> the user may use to log in.</returns>
        public static OAuthPrompt LoginPromptDialog()
        {
            return new OAuthPrompt(
                LoginPrompt,
                new OAuthPromptSettings
                {
                    ConnectionName = AuthConnectionName,
                    Text = "Click to sign in!",
                    Title = "Sign in",
                    Timeout = 300000, // User has 5 minutes to login (1000 * 60 * 5)
                });
        }

        /// <summary>
        /// This <see cref="WaterfallStep"/> prompts the user to log in using the OAuth provider specified by the connection name.
        /// </summary>
        /// <param name="step">A <see cref="WaterfallStepContext"/> provides context for the current waterfall step.</param>
        /// <param name="cancellationToken" >(Optional) A <see cref="CancellationToken"/> that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A <see cref="Task"/> representing the operation result of the operation.</returns>
        private static async Task<DialogTurnResult> PromptStepAsync(WaterfallStepContext step, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await step.BeginDialogAsync(LoginPrompt, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// In this step we check that a token was received and prompt the user as needed.
        /// </summary>
        /// <param name="step">A <see cref="WaterfallStepContext"/> provides context for the current waterfall step.</param>
        /// <param name="cancellationToken" >(Optional) A <see cref="CancellationToken"/> that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A <see cref="Task"/> representing the operation result of the operation.</returns>
        private static async Task<DialogTurnResult> LoginStepAsync(WaterfallStepContext step, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Get the token from the previous step. Note that we could also have gotten the
            // token directly from the prompt itself. There is an example of this in the next method.
            var tokenResponse = (TokenResponse)step.Result;
            if (tokenResponse == null)
            {
                await step.Context.SendActivityAsync("Login was not successful, please try again.", cancellationToken: cancellationToken);
                return EndOfTurn;
            }

            await step.Context.SendActivityAsync("You are now logged in.", cancellationToken: cancellationToken);

            var api = new CarwashService(tokenResponse.Token);
            var reservations = await api.GetMyActiveReservations(cancellationToken);
            switch (reservations.Count)
            {
                case 0:
                    await step.Context.SendActivityAsync("No pending reservations. Get started by making a new reservation!", cancellationToken: cancellationToken);
                    break;
                case 1:
                    await step.Context.SendActivityAsync("I have found an active reservation!", cancellationToken: cancellationToken);
                    break;
                default:
                    await step.Context.SendActivityAsync($"Nice! You have {reservations.Count} reservations in-progress.", cancellationToken: cancellationToken);
                    break;
            }

            return EndOfTurn;

            //return await step.PromptAsync(
            //    DisplayTokenPrompt,
            //    new PromptOptions
            //    {
            //        Prompt = MessageFactory.Text("Would you like to view your token?"),
            //        Choices = new List<Choice> { new Choice("Yes"), new Choice("No") },
            //    },
            //    cancellationToken);
        }

        /// <summary>
        /// Fetch the token and display it for the user if they asked to see it.
        /// </summary>
        /// <param name="step">A <see cref="WaterfallStepContext"/> provides context for the current waterfall step.</param>
        /// <param name="cancellationToken" >(Optional) A <see cref="CancellationToken"/> that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A <see cref="Task"/> representing the operation result of the operation.</returns>
        private static async Task<DialogTurnResult> DisplayTokenAsync(WaterfallStepContext step, CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = (bool)step.Result;
            if (result)
            {
                // Call the prompt again because we need the token. The reasons for this are:
                // 1. If the user is already logged in we do not need to store the token locally in the bot and worry
                // about refreshing it. We can always just call the prompt again to get the token.
                // 2. We never know how long it will take a user to respond. By the time the
                // user responds the token may have expired. The user would then be prompted to login again.
                //
                // There is no reason to store the token locally in the bot because we can always just call
                // the OAuth prompt to get the token or get a new token if needed.
                var prompt = await step.BeginDialogAsync(LoginPrompt, cancellationToken: cancellationToken);
                var tokenResponse = (TokenResponse)prompt.Result;
                if (tokenResponse != null)
                {
                    await step.Context.SendActivityAsync($"Here is your token {tokenResponse.Token}", cancellationToken: cancellationToken);
                }
            }

            return EndOfTurn;
        }
    }
}
