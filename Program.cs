using Azure;
using Azure.AI.OpenAI;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Core.Pipeline;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Identity.Client;
using System.Net.Http.Headers;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client.Extensions;
using Microsoft.Xrm.Sdk;
using System.Text.Json;
using CliWrap.EventStream;
using Microsoft.AspNetCore.Identity;
using Azure.Communication.JobRouter;
using Microsoft.Crm.Sdk.Messages;

var builder = WebApplication.CreateBuilder(args);

//Get ACS Connection String from appsettings.json
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

//Call Automation Client
var client = new CallAutomationClient(connectionString: acsConnectionString);

//Grab the Cognitive Services endpoint from appsettings.json
var cognitiveServicesEndpoint = builder.Configuration.GetValue<string>("CognitiveServiceEndpoint");
ArgumentNullException.ThrowIfNullOrEmpty(cognitiveServicesEndpoint);

string helloPrompt = "Thank you for calling the Bronx Defenders Early Defense Hotline.";
string firstPrompt = "For English, press 1. Para hablar con alguien en español oprima el dos. If your spoken language was not listed, press 3.";
string callerIssuePrompt = "If you are calling because the police have arrested or are looking to arrest or speak to you or a family member, press 1. If you are calling about an ACS/Family Court related issue, press 2. If you are calling about another issue press 3";
string callerIssuePromptSpanish = "Si llama porque la policía lo han arrestado o buscan arrestar o hablar con usted o un miembro de su familia, presione 1. Si llama por un problema relacionado con ACS (Administración de Servicios Infantiles) o corte de familia presione 2. Si llama por otro problema, presione 3";
string goodbyePrompt = "Thank you for calling! I hope I was able to assist you. Have a great day!";
string goodbyePromptSpanish = "¡Gracias por llamar! Espero haber podido ayudarte. ¡Que tengas un gran día!";
string agentPhoneNumberEmptyPrompt = "I'm sorry, we're currently experiencing high call volumes and all of our agents are currently busy. Our next available agent will call you back as soon as possible.";
string agentPhoneNumberPrompt = "You will be connected to an agent shortly";
string agentPhoneNumberPromptSpanish = "Serás conectado con un agente en breve";
string criminalVoicemail = " Hello, you have reached the Bronx Defenders Early Defense Team Hotline. If you have a question or you seek representation for a matter where you are not represented, please leave your name, number and a brief explanation of the issue. Most importantly, please do not speak to the police or other government officials until you have spoken to an attorney.";
string criminalVoicemailSpanish = "Hola, usted se ha comunicado con la línea directa del equipo de primera defensa de Los Bronx Defenders. Si tiene alguna pregunta o busca representación para un asunto en la cual no tiene representación, deje su nombre, número de teléfono y una breve explicación del problema. Lo más importante es que no hable con la policía o otros oficiales hasta haber hablado con un abogado.";
string familyVoicemail = "You've reached the Bronx Defenders Family Defense hotline. Please leave your name and phone number so someone can get back to you.";
string familyVoicemailSpanish = "Hola, Usted ha contactado a Bronx Defenders, la linea directa de la practica familiar. Por favor deje su nombre y su numero de telefono y alguien le devolvera la llamada. Gracias.";
string otherVoicemail = "Please leave a message explaining your issue. If this concerns a legal matter, please do not speak to anyone until you have spoken to someone from our office. Thank you.";
string otherVoicemailSpanish = "Por favor deje un mensaje con una explicación del problema.  Si esto es sobre un problema legal, no hable con otra persona antes de hablar con alguien de nuestra oficina.";
string recordingPrompt = "You will be redirected to voicemail, start recording your message";
string recordingPromptSpanish = "Se le redirigirá al correo de voz, comience a grabar su mensaje";
string recordingStatusPrompt = "Recording Download Location : {_contentLocation}, Recording Delete Location: {_deleteLocation}";

string timeoutSilencePrompt = "I'm sorry, I didn't hear anything. If you need assistance please let me know how I can help you.";
string connectAgentPrompt = "I'm sorry, I was not able to assist you with your request. Let me transfer you to an agent who can help you further. Please hold the line and I'll connect you shortly.";
string callTransferFailurePrompt = "It looks like all I can't connect you to an agent right now, but we will get the next available agent to call you back as soon as possible.";
string EndCallPhraseToConnectAgent = "Sure, please stay on the line. I'm going to transfer you to an agent.";

string transferFailedContext = "TransferFailed";
string connectAgentContext = "ConnectAgent";
string criminalVoiceMailContext = "CriminalVoicemail";
string goodbyeContext = "Goodbye";
  
string agentPhonenumber = builder.Configuration.GetValue<string>("AgentPhoneNumber");

string voiceMailRecordingContentLocation = "";
string voiceMailRecordingMetadataLocation = "";
string voiceMailRecordingDeleteLocation = "";

string languageSelected = String.Empty;
string issueSelected = String.Empty;
string selectedAgentId = String.Empty;
bool callTransferredToAgent = false;

AnswerCallResult answerCallResult;
string callConnectionId = String.Empty;

var key = builder.Configuration.GetValue<string>("AzureOpenAIServiceKey");
ArgumentNullException.ThrowIfNullOrEmpty(key);

var endpoint = builder.Configuration.GetValue<string>("AzureOpenAIServiceEndpoint");
ArgumentNullException.ThrowIfNullOrEmpty(endpoint);

//Register and make CallAutomationClient accessible via dependency injection
builder.Services.AddSingleton(client);
var app = builder.Build();

var devTunnelUri = builder.Configuration.GetValue<string>("DevTunnelUri");
ArgumentNullException.ThrowIfNullOrEmpty(devTunnelUri);
var maxTimeout = 2;

app.MapGet("/", () => "Welcome to Bronx Defenders IVR System!");

// Dataverse related config settings
var dataverseUri = builder.Configuration.GetValue<string>("DataverseUri");
var dataverseClientId = builder.Configuration.GetValue<string>("DataverseClientId");
var dataverseClientSecret = builder.Configuration.GetValue<string>("DataverseClientSecret");
var dataverseTenantId = builder.Configuration.GetValue<string>("DataverseTenantId");
var dataverseConnectionString = builder.Configuration.GetValue<string>("DataverseConnectionString");
    
var jobRouterClient = new JobRouterClient(acsConnectionString);
var jobRouterAdministrationClient = new JobRouterAdministrationClient(acsConnectionString);

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    // Process incoming call event
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"Incoming Call event received.");

        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event for webhook configuration.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var callerId = Helper.GetCallerId(jsonObject);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        logger.LogInformation($"Callback Url: {callbackUri}");
        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            CallIntelligenceOptions = new CallIntelligenceOptions() { CognitiveServicesEndpoint = new Uri(cognitiveServicesEndpoint) }
        };

        answerCallResult = await client.AnswerCallAsync(options);
        logger.LogInformation($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");

        //Use EventProcessor to process CallConnected event
        var answer_result = await answerCallResult.WaitForEventProcessorAsync();
        if (answer_result.IsSuccess)
        {
            callConnectionId = answer_result.SuccessResult.CallConnectionId;
            logger.LogInformation($"Call connected event received for connection id: {answer_result.SuccessResult.CallConnectionId}");
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();
            await HandlePlayAsync(helloPrompt, "Hello Prompt", callConnectionMedia);
            await HandleRecognizeAsync(callConnectionMedia, callerId, firstPrompt);

            // Start continuous DTMF recognition
            await StartContinousDTMFRecognition(client, answerCallResult.CallConnection.CallConnectionId, callerId);

            // Provision workers with JobRouter

            var distributionPolicy = await jobRouterAdministrationClient.CreateDistributionPolicyAsync(
                new CreateDistributionPolicyOptions(
                    distributionPolicyId: "BronxDefendersDistributionPolicy",
                    offerExpiresAfter: TimeSpan.FromMinutes(1),
                    mode: new LongestIdleMode())
                {
                    Name = "Bronx Defenders Distribution Policy",
                }
            );

            var queue = await jobRouterAdministrationClient.CreateQueueAsync(
                new CreateQueueOptions(queueId: "BF-Q1", distributionPolicyId: distributionPolicy.Value.Id)
                {
                    Name = "Bronx Defenders Call Queue",
                }); 
        
            //await ProvisionWorkersWithJobRouter(jobRouterClient, "BF-Q1", logger);
            /*await CreateWorker(jobRouterClient, "BF-Q1", "7fdf9acb-e8b0-ef11-b8e9-000d3a380890", "English", "Family", "", "", logger);
            await CreateWorker(jobRouterClient, "BF-Q1", "7fdf9acb-e8b0-ef11-b8e9-000d3a380890", "English", "Police", "", "", logger);
            await CreateWorker(jobRouterClient, "BF-Q1", "df655973-22b7-ef11-b8e9-000d3a380890", "English", "Other", "", "", logger);*/
            
        }
        client.GetEventProcessor().AttachOngoingEventProcessor<PlayCompleted>(answerCallResult.CallConnection.CallConnectionId, async (playCompletedEvent) =>
        {
            logger.LogInformation($"Play completed event received for connection id: {playCompletedEvent.CallConnectionId}.");
            if (!string.IsNullOrWhiteSpace(playCompletedEvent.OperationContext) && (playCompletedEvent.OperationContext.Equals(transferFailedContext, StringComparison.OrdinalIgnoreCase) 
                || playCompletedEvent.OperationContext.Equals(goodbyeContext, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogInformation($"Disconnecting the call...");
                await answerCallResult.CallConnection.HangUpAsync(true);
            }
            else if (!string.IsNullOrWhiteSpace(playCompletedEvent.OperationContext) && playCompletedEvent.OperationContext.Equals(connectAgentContext, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(agentPhonenumber))
                {
                    logger.LogInformation($"Agent phone number is empty");
                    await HandlePlayAsync(agentPhoneNumberEmptyPrompt,
                      transferFailedContext, answerCallResult.CallConnection.GetCallMedia());
                }
                else
                {
                    logger.LogInformation($"Initializing the Call transfer...");
                    CommunicationIdentifier transferDestination = new PhoneNumberIdentifier(agentPhonenumber);
                    TransferCallToParticipantResult result = await answerCallResult.CallConnection.TransferCallToParticipantAsync(transferDestination);
                    logger.LogInformation($"Transfer call initiated: {result.OperationContext}");
                }
            }
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<ContinuousDtmfRecognitionToneReceived>(answerCallResult.CallConnection.CallConnectionId, async (dtmfEvent) =>
        {
            logger.LogInformation($"DTMF tone received: {dtmfEvent.Tone}");
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();
            if (dtmfEvent.Tone.Equals(DtmfTone.One) && dtmfEvent.SequenceId == 1)
            {
                languageSelected = "English";
                await InsertRecordInDataverse(answerCallResult.CallConnection.CallConnectionId, callerId, 1, 0, logger);
                await HandleRecognizeAsync(callConnectionMedia, callerId, callerIssuePrompt);
            }
            else if (dtmfEvent.Tone.Equals(DtmfTone.Two) && dtmfEvent.SequenceId == 1)
            {
                languageSelected = "Spanish";
                await InsertRecordInDataverse(answerCallResult.CallConnection.CallConnectionId, callerId, 2, 0, logger);
                await HandleRecognizeAsync(callConnectionMedia, callerId, callerIssuePromptSpanish);
            }
            else if (dtmfEvent.Tone.Equals(DtmfTone.Three) && dtmfEvent.SequenceId == 1)
            {
                languageSelected = "English";
                await InsertRecordInDataverse(answerCallResult.CallConnection.CallConnectionId, callerId, 3, 0, logger);
                await HandleRecognizeAsync(callConnectionMedia, callerId, callerIssuePrompt);
            }
            if (dtmfEvent.Tone.Equals(DtmfTone.One) && dtmfEvent.SequenceId == 2)
            {
                issueSelected = "Police";
                await UpdateRecordInDataverse(answerCallResult.CallConnection.CallConnectionId, callerId, 1, 1, logger);
                logger.LogInformation($"Initializing the Call transfer...");
                await CreateJob(jobRouterClient, "BF-Q1", languageSelected, issueSelected, logger);

                //await ProvisionWorkersWithJobRouter(jobRouterClient, "BF-Q1", logger);
                //await CheckAgentAvailabilityandTransfer(languageSelected, issueSelected, answerCallResult, logger);
            }
            else if (dtmfEvent.Tone.Equals(DtmfTone.Two) && dtmfEvent.SequenceId == 2)
            {
                issueSelected = "ACS/FamilyCourt";
                await UpdateRecordInDataverse(answerCallResult.CallConnection.CallConnectionId, callerId, 1, 2, logger);
                logger.LogInformation($"Initializing the Call transfer...");
                await CreateJob(jobRouterClient, "BF-Q1", languageSelected, issueSelected, logger);
                //await CreateWorker(jobRouterClient, "BF-Q1", "7fdf9acb-e8b0-ef11-b8e9-000d3a380890", "English", "Family", "", "", logger);
                //await CheckAgentAvailabilityandTransfer(languageSelected, issueSelected, answerCallResult, logger);
            }
            else if (dtmfEvent.Tone.Equals(DtmfTone.Three) && dtmfEvent.SequenceId == 2)
            {
                issueSelected = "Other";
                await UpdateRecordInDataverse(answerCallResult.CallConnection.CallConnectionId, callerId, 1, 3, logger);
                logger.LogInformation($"Initializing the Call transfer...");

                await CreateJob(jobRouterClient, "BF-Q1", languageSelected, issueSelected, logger);
                                await CreateWorker(jobRouterClient, "BF-Q1", "7fdf9acb-e8b0-ef11-b8e9-000d3a380890", "English", "Family", "", "", logger);
                            await CreateWorker(jobRouterClient, "BF-Q1", "df655973-22b7-ef11-b8e9-000d3a380890", "English", "Other", "", "", logger);
                //var theWorker = await CreateWorker(jobRouterClient, "BF-Q1", "7fdf9acb-e8b0-ef11-b8e9-000d3a380890", languageSelected, issueSelected, "", "", logger);

                //await CheckAgentAvailabilityandTransfer(languageSelected, issueSelected, answerCallResult, logger);
            }
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<PlayFailed>(answerCallResult.CallConnection.CallConnectionId, async (playFailedEvent) =>
        {
            logger.LogInformation($"Play failed event received for connection id: {playFailedEvent.CallConnectionId}. Hanging up call...");
            await answerCallResult.CallConnection.HangUpAsync(true);
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<CallDisconnected>(answerCallResult.CallConnection.CallConnectionId, async (callDisconnectedEvent) =>
        {
            logger.LogInformation($"Call transfer accepted event received for connection id: {callDisconnectedEvent.CallConnectionId}.");
            if (callTransferredToAgent)
            {
                await SetCurrentlyHandlingCall(selectedAgentId, false, logger);
            }
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<AnswerFailed>(answerCallResult.CallConnection.CallConnectionId, async (answerFailedEvent) =>
        {
            logger.LogInformation($"Answer failed event received for connection id: {answerFailedEvent.CallConnectionId}.");
            if (callTransferredToAgent)
            {
                await SetCurrentlyHandlingCall(selectedAgentId, false, logger);
            }   
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferAccepted>(answerCallResult.CallConnection.CallConnectionId, async (callTransferAcceptedEvent) =>
        {
            logger.LogInformation($"Call transfer accepted event received for connection id: {callTransferAcceptedEvent.CallConnectionId}.");
            logger.LogInformation($"Call transfer accepted event received for connection id: {callTransferAcceptedEvent.ResultInformation?.Message}.");
            logger.LogInformation($"Call transfer accepted event received for connection id: {callTransferAcceptedEvent.Transferee}.");
        });
        // Try different phone number instead of same one.
        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferFailed>(answerCallResult.CallConnection.CallConnectionId, async (callTransferFailedEvent) =>
        {
            logger.LogInformation($"Call transfer failed event received for connection id: {callTransferFailedEvent.CallConnectionId}.");

            if (callTransferredToAgent)
            {
                await SetCurrentlyHandlingCall(selectedAgentId, false, logger);
            }   

            var resultInformation = callTransferFailedEvent.ResultInformation;
            logger.LogError("Encountered error during call transfer, message={msg}, code={code}, subCode={subCode}", resultInformation?.Message, resultInformation?.Code, resultInformation?.SubCode);

            // Transfer call to voicemail
            await TransferToVoicemail(answerCallResult, callerId, logger);
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeFailed>(answerCallResult.CallConnection.CallConnectionId, async (recognizeFailedEvent) =>
        {
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();

            if (MediaEventReasonCode.RecognizeInitialSilenceTimedOut.Equals(recognizeFailedEvent.ResultInformation.SubCode.Value.ToString()) && maxTimeout > 0)
            {
                logger.LogInformation($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Retrying recognize...");
                maxTimeout--;
                await HandleRecognizeAsync(callConnectionMedia, callerId, timeoutSilencePrompt);
            }
            else
            {
                logger.LogInformation($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Transferring to voicemail...");
                // Transfer call to voicemail
                await TransferToVoicemail(answerCallResult, callerId, logger);
               // await HandlePlayAsync(goodbyePrompt, goodbyeContext, callConnectionMedia);
            }
        });
    }
    return Results.Ok();
});

// api to handle call back events
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    CallAutomationClient callAutomationClient,
    ILogger<Program> logger) =>
{
    var eventProcessor = client.GetEventProcessor();
    eventProcessor.ProcessEvents(cloudEvents);

    return Results.Ok();
});

// api to handle call back events
app.MapPost("/api/recordingFileStatus", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"Incoming Call event received.");

        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        if (eventData is Azure.Messaging.EventGrid.SystemEvents.AcsRecordingFileStatusUpdatedEventData statusUpdated)
        {
            voiceMailRecordingContentLocation = statusUpdated.RecordingStorageInfo.RecordingChunks[0].ContentLocation;
            voiceMailRecordingMetadataLocation = statusUpdated.RecordingStorageInfo.RecordingChunks[0].MetadataLocation;
            voiceMailRecordingDeleteLocation = statusUpdated.RecordingStorageInfo.RecordingChunks[0].DeleteLocation;

            await UpdateRecordVoicemailValueInDataverse(callConnectionId, voiceMailRecordingContentLocation, logger);
            logger.LogInformation($"Recording Location: {voiceMailRecordingContentLocation}\n Recording Metadata: {voiceMailRecordingMetadataLocation}");
        }
    }
    return Results.Ok($"Recording Download Location : {voiceMailRecordingContentLocation}, Recording Delete Location: {voiceMailRecordingDeleteLocation}");
});

app.MapPost("/api/workerOfferEvents", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"Incoming Call event received.");

        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }

            switch (eventGridEvent.EventType)
            {
                case "Microsoft.Communication.RouterWorkerOfferIssued":
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    var worker = await jobRouterClient.GetWorkerAsync("df655973-22b7-ef11-b8e9-000d3a380890");
                    
                    // accepting the offer sent to `worker-1`

                    foreach (var offer in worker.Value.Offers)
                    {
                        Response<AcceptJobOfferResult> acceptJobOfferResult = jobRouterClient.AcceptJobOffer(worker.Value.Id, offer.OfferId);
                        Console.WriteLine($"Worker {worker.Value.Id} has an active offer for job {offer.JobId}");
                    }

                    break;
                case "Microsoft.Communication.RouterWorkerOfferAccepted":
                    logger.LogInformation($"Worker Offer Accepted event received for worker id:");
                    CommunicationIdentifier transferDestination = new PhoneNumberIdentifier("+15109539764");
                    var callConnection = client.GetCallConnection(callConnectionId); // Assign a valid call connection ID
                    var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    TransferCallToParticipantResult result = await callConnection.TransferCallToParticipantAsync(transferDestination,tokenSource.Token);
                    break;
                case "Microsoft.Communication.JobRouter.RouterWorkerOfferDeclined":
                    logger.LogInformation($"Worker Offer Declined event received for worker id");
                    break;
                default:
                    break;
            }
        }       
    }
    return Results.Ok();
});

app.MapGet("/api/downloadVoicemail", async (
    ILogger<Program> logger) =>
{
    var callRecording = client.GetCallRecording();
    callRecording.DownloadTo(new Uri(voiceMailRecordingContentLocation), "Recording_File.wav");
    return Results.Ok();
});

async Task TransferToVoicemail(AnswerCallResult answerCallResult, string callerId, ILogger<Program> logger)
{
    // Transfer to voicemail
    var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();
    if (languageSelected == "English")
    {
        await UpdateRecordInDataverse(answerCallResult.CallConnection.CallConnectionId, callerId, 1, 3, logger);
        await HandleRecognizeAsync(callConnectionMedia, callerId, otherVoicemail);
    }
    else if (languageSelected == "Spanish")
    {
        await UpdateRecordInDataverse(answerCallResult.CallConnection.CallConnectionId, callerId, 2, 3, logger);
        await HandleRecognizeAsync(callConnectionMedia, callerId, otherVoicemailSpanish);
    }
    
    Thread.Sleep(3000);
    // Beep sound
    var beepAudioFile = "https://voicemailrecordingstgacc.blob.core.windows.net/bronxdefendersvoicemails/audiofiles/beep.wav";
    await HandlePlayAudioAsync(beepAudioFile, criminalVoiceMailContext, callConnectionMedia);

    var serverCallId = client.GetCallConnection(answerCallResult.CallConnection.CallConnectionId).GetCallConnectionProperties().Value.ServerCallId;
    var callLocator = new ServerCallLocator(serverCallId);
    StartRecordingOptions recordingOptions = new StartRecordingOptions(callLocator)
    {
        RecordingContent = RecordingContent.Audio,
        RecordingChannel = RecordingChannel.Unmixed,
        RecordingFormat = RecordingFormat.Wav,

        RecordingStorage = RecordingStorage.CreateAzureBlobContainerRecordingStorage(new Uri("https://voicemailrecordingstgacc.blob.core.windows.net/bronxdefendersvoicemails"))
    };

    Response<RecordingStateResult> recordingResponse = await client.GetCallRecording()
    .StartAsync(recordingOptions);

    var recordingId = recordingResponse.Value.RecordingId;
    logger.LogInformation($"Recording started. RecordingId: {recordingId}");
}

// Method to handle recognize
async Task HandleRecognizeAsync(CallMedia callConnectionMedia, string callerId, string message)
{
    // Play greeting message
    var greetingPlaySource = new TextSource(message)
    {
        VoiceName = "en-US-NancyNeural"
    };

    var recognizeOptions =
        new CallMediaRecognizeDtmfOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(callerId), maxTonesToCollect: 3)
        {
            InterruptPrompt = true,
            InitialSilenceTimeout = TimeSpan.FromSeconds(15),
            Prompt = greetingPlaySource,
            OperationContext = "GetFreeFormText"
        };

    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

// Method to play text message
async Task HandlePlayAsync(string textToPlay, string context, CallMedia callConnectionMedia)
{
    // Play message
    var playSource = new TextSource(textToPlay)
    {
        VoiceName = "en-US-NancyNeural"
    };

    var playOptions = new PlayToAllOptions(playSource) { OperationContext = context };
    await callConnectionMedia.PlayToAllAsync(playOptions);
}

// Method to play audio file (Beep.wav)
async Task HandlePlayAudioAsync(string audioFile, string context, CallMedia callConnectionMedia)
{
    // Play message
    var playSource = new FileSource(new Uri(audioFile));

    var playOptions = new PlayToAllOptions(playSource) { OperationContext = context };
    await callConnectionMedia.PlayToAllAsync(playOptions);
}

async Task StartContinousDTMFRecognition(CallAutomationClient client, string callConnectionId, string callerId)
{
    var cId = callerId.Substring(2);

    await client.GetCallConnection(callConnectionId)
    .GetCallMedia()
    .StartContinuousDtmfRecognitionAsync(new PhoneNumberIdentifier(cId));
}

// Add a method to insert record in dataverse table
async Task InsertRecordInDataverse(string connectionId, string phoneNumber, int languageSelected, int issueSelected, ILogger<Program> logger)
{
    var dataverseClient = ConfidentialClientApplicationBuilder.Create(dataverseClientId)
            .WithClientSecret(dataverseClientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{dataverseTenantId}"))
            .Build();

    // Acquire token for Dataverse
    var authResult = dataverseClient.AcquireTokenForClient(new[] { $"{dataverseUri}/.default" }).ExecuteAsync().Result;
    var accessToken = authResult.AccessToken;

    ILogger<ServiceClient> serviceClientLogger = app.Services.GetRequiredService<ILogger<ServiceClient>>();
    ServiceClient _serviceClient = new ServiceClient(dataverseConnectionString);

    // Create a new record in the dataverse table
    Entity entity = new Entity("bxd_event");
    entity["bxd_acscallconnectionid"] = connectionId;
    entity["bxd_phonenumber"] = phoneNumber.Substring(2);
    entity.Attributes.Add("bxd_languagepromptselectioncode", new OptionSetValue(languageSelected));
    if (issueSelected != 0)
    {
        entity.Attributes.Add("bxd_issuepromptselectioncode", new OptionSetValue(issueSelected));
    }

    var response = _serviceClient.CreateAsync(entity).Result;
    logger.LogInformation($"Record created in Dataverse: {response.ToString()}");
}

// Update dataverse record
async Task UpdateRecordInDataverse(string connectionId, string phoneNumber, int languageSelected, int issueSelected, ILogger<Program> logger)
{
    var dataverseClient = ConfidentialClientApplicationBuilder.Create(dataverseClientId)
            .WithClientSecret(dataverseClientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{dataverseTenantId}"))
            .Build();

    // Acquire token for Dataverse
    var authResult = dataverseClient.AcquireTokenForClient(new[] { $"{dataverseUri}/.default" }).ExecuteAsync().Result;
    var accessToken = authResult.AccessToken;

    ILogger<ServiceClient> serviceClientLogger = app.Services.GetRequiredService<ILogger<ServiceClient>>();
    ServiceClient _serviceClient = new ServiceClient(dataverseConnectionString);

    // Create a new record in the dataverse table
    // Update the record in the dataverse table using connection id

    var existingRecord = _serviceClient.RetrieveMultipleAsync(new QueryExpression("bxd_event")
    {
        ColumnSet = new ColumnSet("bxd_eventid"),
        Criteria = new FilterExpression
        {
            Conditions =
            {
                new ConditionExpression("bxd_acscallconnectionid", ConditionOperator.Equal, connectionId)
            }
        }
    }).Result.Entities.FirstOrDefault();

    if (existingRecord == null)
    {
        logger.LogInformation($"Record not found in Dataverse for connection id: {connectionId}");
        return;
    }

    if (existingRecord != null)
    {
        existingRecord.Attributes.Add("bxd_issuepromptselectioncode", new OptionSetValue(issueSelected));
        
        var response = _serviceClient.UpdateAsync(existingRecord);
        logger.LogInformation($"Record updated with the issue selected: {response.ToString()}");
    }
}

// Update dataverse record
async Task UpdateRecordVoicemailValueInDataverse(string connectionId, string voicemailUrl, ILogger<Program> logger)
{
    var dataverseClient = ConfidentialClientApplicationBuilder.Create(dataverseClientId)
            .WithClientSecret(dataverseClientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{dataverseTenantId}"))
            .Build();

    // Acquire token for Dataverse
    var authResult = dataverseClient.AcquireTokenForClient(new[] { $"{dataverseUri}/.default" }).ExecuteAsync().Result;
    var accessToken = authResult.AccessToken;

    ILogger<ServiceClient> serviceClientLogger = app.Services.GetRequiredService<ILogger<ServiceClient>>();
    ServiceClient _serviceClient = new ServiceClient(dataverseConnectionString);

    // Create a new record in the dataverse table
    // Update the record in the dataverse table using connection id

    var existingRecord = _serviceClient.RetrieveMultipleAsync(new QueryExpression("bxd_event")
    {
        ColumnSet = new ColumnSet("bxd_eventid"),
        Criteria = new FilterExpression
        {
            Conditions =
            {
                new ConditionExpression("bxd_acscallconnectionid", ConditionOperator.Equal, connectionId)
            }
        }
    }).Result.Entities.FirstOrDefault();

    if (existingRecord == null)
    {
        logger.LogInformation($"Record not found in Dataverse for connection id: {connectionId}");
        return;
    }

    if (existingRecord != null)
    {
        existingRecord.Attributes.Add("bxd_voicemailurl", voicemailUrl);
        
        var response = _serviceClient.UpdateAsync(existingRecord);
        logger.LogInformation($"Record updated with voicemail link: {response.ToString()}");
    }
}

async Task SetCurrentlyHandlingCall(string agentid, bool availability, ILogger<Program> logger)
{
    var dataverseClient = ConfidentialClientApplicationBuilder.Create(dataverseClientId)
            .WithClientSecret(dataverseClientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{dataverseTenantId}"))
            .Build();

    // Acquire token for Dataverse
    var authResult = dataverseClient.AcquireTokenForClient(new[] { $"{dataverseUri}/.default" }).ExecuteAsync().Result;
    var accessToken = authResult.AccessToken;

    ILogger<ServiceClient> serviceClientLogger = app.Services.GetRequiredService<ILogger<ServiceClient>>();
    ServiceClient _serviceClient = new ServiceClient(dataverseConnectionString);

    // Create a new record in the dataverse table
    // Update the record in the dataverse table using connection id

    var existingRecord = _serviceClient.RetrieveMultipleAsync(new QueryExpression("bxd_agent")
    {
        ColumnSet = new ColumnSet("bxd_agentid"),
        Criteria = new FilterExpression
        {
            Conditions =
            {
                new ConditionExpression("bxd_agentid", ConditionOperator.Equal, agentid)
            }
        }
    }).Result.Entities.FirstOrDefault();

    if (existingRecord == null)
    {
        logger.LogInformation($"Record not found in Dataverse for agent id: {agentid}");
        return;
    }

    if (existingRecord != null)
    {
        existingRecord.Attributes.Add("bxd_iscurrentlyhandlingcall",  availability);
        
        var response = _serviceClient.UpdateAsync(existingRecord);
    }
}

// Check agent availability and transfer the call
async Task CheckAgentAvailabilityandTransfer(string languageSkill, string issueSkill, AnswerCallResult answerCallResult, ILogger<Program> logger)
{
    var dataverseClient = ConfidentialClientApplicationBuilder.Create(dataverseClientId)
            .WithClientSecret(dataverseClientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{dataverseTenantId}"))
            .Build();

    // Acquire token for Dataverse
    var authResult = dataverseClient.AcquireTokenForClient(new[] { $"{dataverseUri}/.default" }).ExecuteAsync().Result;
    var accessToken = authResult.AccessToken;

    ILogger<ServiceClient> serviceClientLogger = app.Services.GetRequiredService<ILogger<ServiceClient>>();
    ServiceClient _serviceClient = new ServiceClient(dataverseConnectionString);

    //create a fetchxml query to retrieve agent schedule
    var scheduleFetchXml = $@"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
        <entity name='bxd_schedule'>
            <attribute name='statecode' />
            <attribute name='bxd_scheduleid' />
            <attribute name='bxd_name' />
            <attribute name='bxd_scheduledstart' />
            <attribute name='bxd_scheduledend' />
            <attribute name='bxd_availabilitytypecode' />
            <attribute name='bxd_agentid' />
            <filter type='and'>
                <condition attribute='statecode' operator='eq' value='0' />
                <condition attribute='bxd_scheduledstart' operator='today'/>
            </filter>
            <link-entity name='bxd_agent' from='bxd_agentid' to='bxd_agentid' alias='agent'>
                <attribute name='bxd_name' />
                <attribute name='bxd_primaryphone' />
            </link-entity>
        </entity>
    </fetch>";

    // Retrieve agents schedule
    EntityCollection responseSchedule = _serviceClient.RetrieveMultipleAsync(new FetchExpression(scheduleFetchXml)).Result;
    List<KeyValuePair<string, string[]>> agentScheduleDict = new List<KeyValuePair<string, string[]>>();

    foreach (var schedule in responseSchedule.Entities)
    {
        var scheduleId = schedule.Attributes["bxd_scheduleid"]?.ToString();
        var scheduleName = schedule.Attributes["bxd_name"]?.ToString();
        var scheduleTypeCode = schedule.Attributes["bxd_availabilitytypecode"]?.ToString();
        var scheduleAgentId = schedule.Attributes["bxd_agentid"]?.ToString();
        var scheduleStart = schedule.Attributes["bxd_scheduledstart"]?.ToString();
        var scheduleEnd = schedule.Attributes["bxd_scheduledend"]?.ToString();
        var agentName = schedule.GetAttributeValue<AliasedValue>("agent.bxd_name").Value.ToString();
        var agentPrimaryPhone = schedule.GetAttributeValue<AliasedValue>("agent.bxd_primaryphone").Value.ToString();

        if (!string.IsNullOrEmpty(scheduleName) && !string.IsNullOrEmpty(scheduleTypeCode) && !string.IsNullOrEmpty(scheduleAgentId) && !string.IsNullOrEmpty(scheduleStart) && !string.IsNullOrEmpty(scheduleEnd))
        {
            // Add agent name as the key and other values as array of values
            if (agentName != null && agentPrimaryPhone != null)
            {
                agentScheduleDict.Add(new KeyValuePair<string, string[]>(scheduleName, new string[] { agentName, agentPrimaryPhone, scheduleTypeCode, scheduleAgentId, scheduleStart, scheduleEnd }));
            }
        }
        else
        {

        }
    }

    //create a fetchxml query to retrieve agent skills
    var fetchXml = $@"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
        <entity name='bxd_agent'>
            <attribute name='bxd_name' />
            <attribute name='bxd_agentid' />
            <attribute name='bxd_primaryphone' />
            <attribute name='bxd_iscurrrentlyhandlingcall' />
            <link-entity name='bxd_agent_bxd_skills' from='bxd_agentid' to='bxd_agentid' intersect='true'>
                <link-entity name='bxd_skills' from='bxd_skillsid' to='bxd_skillsid' link-type='inner' alias='skills'>
                    <attribute name='bxd_name' />
                    <attribute name='bxd_skillsid' />
                </link-entity>
            </link-entity>
        </entity>
    </fetch>";

    // Retrieve agents skills
    EntityCollection responseSkills = _serviceClient.RetrieveMultipleAsync(new FetchExpression(fetchXml)).Result;
    List<KeyValuePair<string, string[]>> agentSkillsDict = new List<KeyValuePair<string, string[]>>();

    foreach (var agent in responseSkills.Entities)
    {
        var agentId = agent.Attributes["bxd_agentid"].ToString();
        var agentName = agent.Attributes["bxd_name"].ToString();
        var agentPrimaryPhone = agent.Attributes["bxd_primaryphone"].ToString();
        var agentIsCurrentlyHandlingCall = (bool)agent.Attributes["bxd_iscurrrentlyhandlingcall"];  
        var agentSkill = agent.GetAttributeValue<AliasedValue>("skills.bxd_name").Value.ToString();
        var agentSkillsId = agent.GetAttributeValue<AliasedValue>("skills.bxd_skillsid").Value.ToString();

        if (!string.IsNullOrEmpty(agentId) && !string.IsNullOrEmpty(agentName) && !string.IsNullOrEmpty(agentPrimaryPhone) && !string.IsNullOrEmpty(agentSkill))
        {
            // Add agent name as the key and other values as array of values
            agentSkillsDict.Add(new KeyValuePair<string, string[]>(agentName, new string[] { agentId, agentName, agentPrimaryPhone, agentSkill, agentIsCurrentlyHandlingCall.ToString() }));
        }
        else
        {

        }
    }

    // Check which agent is available based on schedule
    List<KeyValuePair<string, string>> agentAvailableAgentList = new List<KeyValuePair<string, string>>();
    foreach (var agent in agentScheduleDict)
    {
        var agentName = agent.Value[0];
        var agentPrimaryPhone = agent.Value[1];
        var agentSkill = agent.Value[2];
        var agentId = agent.Value[3];
        var agentStart = agent.Value[4];
        var agentEnd = agent.Value[5];

        // Check now is between agentStart and agentEnd, add the agent to the list
        if (DateTime.Now >= DateTime.Parse(agentStart) && DateTime.Now <= DateTime.Parse(agentEnd))
        {
            //await CreateJob(jobRouterClient, "BF-Q1", languageSkill, issueSkill, logger);
            // Add the agent to the list
            agentAvailableAgentList.Add(new KeyValuePair<string, string>(agentName, agentPrimaryPhone));
        }
    }

    // Check if the agent is available from dataverse
    foreach (var agent in agentSkillsDict)
    {
        var agentId = agent.Value[0];
        var agentName = agent.Value[1];
        var agentPrimaryPhone = agent.Value[2];
        var agentSkill = agent.Value[3];
        var agentIsCurrentlyHandlingCall = agent.Value[4];

        if (agentAvailableAgentList.Any(agent => agent.Key == agentName))
        {
            if (agentSkill == languageSkill || agentSkill == issueSkill)
            {
                // If the agent is available, transfer the call to the agent
                if (agentIsCurrentlyHandlingCall == "False")
                {
                    selectedAgentId = agentId;
                    callTransferredToAgent = true;

                    CommunicationIdentifier transferDestination = new PhoneNumberIdentifier(agentPrimaryPhone);
                    //TransferCallToParticipantResult result = await answerCallResult.CallConnection.TransferCallToParticipantAsync(transferDestination);
                    await SetCurrentlyHandlingCall(agentId, true, logger);
                    //logger.LogInformation($"Transfer call initiated: {result.OperationContext}");
                    // exit for loop
                    break;
                }
                else
                {
                    logger.LogInformation($"Agent is currently handling a call. Trying next agent...");
                }
            }
        }   
    }
}

async Task ProvisionWorkersWithJobRouter(JobRouterClient routerClient, string queueId, ILogger<Program> logger)
{
    var dataverseClient = ConfidentialClientApplicationBuilder.Create(dataverseClientId)
            .WithClientSecret(dataverseClientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{dataverseTenantId}"))
            .Build();

    // Acquire token for Dataverse
    var authResult = dataverseClient.AcquireTokenForClient(new[] { $"{dataverseUri}/.default" }).ExecuteAsync().Result;
    var accessToken = authResult.AccessToken;

    ILogger<ServiceClient> serviceClientLogger = app.Services.GetRequiredService<ILogger<ServiceClient>>();
    ServiceClient _serviceClient = new ServiceClient(dataverseConnectionString);

    //create a fetchxml query to retrieve agent schedule
    var scheduleFetchXml = $@"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
        <entity name='bxd_schedule'>
            <attribute name='statecode' />
            <attribute name='bxd_scheduleid' />
            <attribute name='bxd_name' />
            <attribute name='bxd_scheduledstart' />
            <attribute name='bxd_scheduledend' />
            <attribute name='bxd_availabilitytypecode' />
            <attribute name='bxd_agentid' />
            <filter type='and'>
                <condition attribute='statecode' operator='eq' value='0' />
                <condition attribute='bxd_scheduledstart' operator='today'/>
            </filter>
            <link-entity name='bxd_agent' from='bxd_agentid' to='bxd_agentid' alias='agent'>
                <attribute name='bxd_name' />
                <attribute name='bxd_primaryphone' />
            </link-entity>
        </entity>
    </fetch>";

    // Retrieve agents schedule
    EntityCollection responseSchedule = _serviceClient.RetrieveMultipleAsync(new FetchExpression(scheduleFetchXml)).Result;
    List<KeyValuePair<string, string[]>> agentScheduleDict = new List<KeyValuePair<string, string[]>>();

    foreach (var schedule in responseSchedule.Entities)
    {
        var scheduleId = schedule.Attributes["bxd_scheduleid"]?.ToString();
        var scheduleName = schedule.Attributes["bxd_name"]?.ToString();
        var scheduleTypeCode = schedule.Attributes["bxd_availabilitytypecode"]?.ToString();
        var scheduleAgentId = schedule.Attributes["bxd_agentid"]?.ToString();
        var scheduleStart = schedule.Attributes["bxd_scheduledstart"]?.ToString();
        var scheduleEnd = schedule.Attributes["bxd_scheduledend"]?.ToString();
        var agentName = schedule.GetAttributeValue<AliasedValue>("agent.bxd_name").Value.ToString();
        var agentPrimaryPhone = schedule.GetAttributeValue<AliasedValue>("agent.bxd_primaryphone").Value.ToString();

        if (!string.IsNullOrEmpty(scheduleName) && !string.IsNullOrEmpty(scheduleTypeCode) && !string.IsNullOrEmpty(scheduleAgentId) && !string.IsNullOrEmpty(scheduleStart) && !string.IsNullOrEmpty(scheduleEnd))
        {
            // Add agent name as the key and other values as array of values
            if (agentName != null && agentPrimaryPhone != null)
            {
                agentScheduleDict.Add(new KeyValuePair<string, string[]>(agentName, new string[] { agentName, agentPrimaryPhone, scheduleTypeCode, scheduleAgentId, scheduleStart, scheduleEnd }));
            }
        }
        else
        {
        }
    }

    //create a fetchxml query to retrieve agent skills
    var fetchXml = $@"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
        <entity name='bxd_agent'>
            <attribute name='bxd_name' />
            <attribute name='bxd_agentid' />
            <attribute name='bxd_primaryphone' />
            <attribute name='bxd_iscurrrentlyhandlingcall' />
            <link-entity name='bxd_agent_bxd_skills' from='bxd_agentid' to='bxd_agentid' intersect='true'>
                <link-entity name='bxd_skills' from='bxd_skillsid' to='bxd_skillsid' link-type='inner' alias='skills'>
                    <attribute name='bxd_name' />
                    <attribute name='bxd_skillsid' />
                </link-entity>
            </link-entity>
        </entity>
    </fetch>";

    // Retrieve agents skills
    EntityCollection responseSkills = _serviceClient.RetrieveMultipleAsync(new FetchExpression(fetchXml)).Result;
    List<KeyValuePair<string, string[]>> agentSkillsDict = new List<KeyValuePair<string, string[]>>();

    foreach (var agent in responseSkills.Entities)
    {
        var agentId = agent.Attributes["bxd_agentid"].ToString();
        var agentName = agent.Attributes["bxd_name"].ToString();
        var agentPrimaryPhone = agent.Attributes["bxd_primaryphone"].ToString();
        var agentIsCurrentlyHandlingCall = (bool)agent.Attributes["bxd_iscurrrentlyhandlingcall"];  
        var agentSkill = agent.GetAttributeValue<AliasedValue>("skills.bxd_name").Value.ToString();
        var agentSkillsId = agent.GetAttributeValue<AliasedValue>("skills.bxd_skillsid").Value.ToString();
        //var agentSchedule = agentScheduleDict.FirstOrDefault(x => x.Key == agentName);

        if (!string.IsNullOrEmpty(agentId) && !string.IsNullOrEmpty(agentName) && !string.IsNullOrEmpty(agentPrimaryPhone) && !string.IsNullOrEmpty(agentSkill))
        {
            // Add agent name as the key and other values as array of values
            agentSkillsDict.Add(new KeyValuePair<string, string[]>(agentName, new string[] { agentId, agentName, agentPrimaryPhone, agentSkill, agentIsCurrentlyHandlingCall.ToString() }));
            //agentSkillsDict.Add(new KeyValuePair<string, string[]>(agentName, new string[] { agentId, agentName, agentPrimaryPhone, agentSkill, agentIsCurrentlyHandlingCall.ToString(), agentSchedule.Value[4], agentSchedule.Value[5] }));
        }
        else
        {

        }
    }

    // Check if the agent is available from dataverse
    /*foreach (var agent in agentSkillsDict)
    {
        var agentId = agent.Value[0];
        var agentName = agent.Value[1];
        var agentPrimaryPhone = agent.Value[2];
        var agentSkill = agent.Value[3];
        var agentIsCurrentlyHandlingCall = agent.Value[4];
        var agentStart = agent.Value[5];
        var agentEnd = agent.Value[6];

        //await CreateWorker(routerClient, queueId, agentId, agentSkill, agentSkill, agentStart, agentEnd, logger);
        await CreateWorker(routerClient, queueId, agentId, languageSelected, issueSelected, "", "", logger);
 
    } */
    await CreateWorker(routerClient, queueId, "7fdf9acb-e8b0-ef11-b8e9-000d3a380890", "English", "Family", "", "", logger);
    await CreateWorker(routerClient, queueId, "7fdf9acb-e8b0-ef11-b8e9-000d3a380890", "English", "Police", "", "", logger);
    await CreateWorker(routerClient, queueId, "df655973-22b7-ef11-b8e9-000d3a380890", "English", "Other", "", "", logger);
}

async Task<Response<RouterWorker>> CreateWorker(JobRouterClient routerClient, string queueId,  string agentId, string languageSkill, string issueSkill, string startDate, string endDate, ILogger<Program> logger)
{
    var worker = await routerClient.CreateWorkerAsync(
        new CreateWorkerOptions(workerId: agentId, capacity: 1)
        {
            Queues = { queueId },
            Labels = { 
                ["Language"] = new RouterValue(languageSkill), 
                ["Issue"] = new RouterValue(issueSkill),
            },
            Channels = { new RouterChannel(channelId: "BFVoiceCall", capacityCostPerJob: 1) },
            AvailableForOffers = true
        });

    return worker ?? throw new InvalidOperationException("Worker creation failed.");
}

 /* create the job for the worker */
async Task<Response<RouterJob>> CreateJob(JobRouterClient routerClient, string queueId, string languageSkill, string issueSkill, ILogger<Program> logger)
{
    var jobId = Guid.NewGuid().ToString();
    var job = await routerClient.CreateJobAsync(new CreateJobOptions(jobId, "BFVoiceCall", queueId)
    {
        Priority = 1,
        RequestedWorkerSelectors =
        {
            new RouterWorkerSelector(key: "Language", labelOperator: LabelOperator.Equal, value: new RouterValue(languageSkill)),
            new RouterWorkerSelector(key: "Issue", labelOperator: LabelOperator.Equal, value: new RouterValue(issueSkill))
        }
    });

    return job ?? throw new InvalidOperationException("Job creation failed.");
}

app.MapGet("/api/transferCall", async (
    ILogger<Program> logger) =>
{
    var callConnection = client.GetCallConnection("callConnectionId");
    var transferDestination = new PhoneNumberIdentifier("phoneNumber");
    var result = await callConnection.TransferCallToParticipantAsync(transferDestination);
    return Results.Ok(result);
});

app.MapGet("/api/hangupCall", async (
    ILogger<Program> logger) =>
{
    var callConnection = client.GetCallConnection("callConnectionId");
    var result = await callConnection.HangUpAsync(true);
    return Results.Ok(result);
});

app.Run();