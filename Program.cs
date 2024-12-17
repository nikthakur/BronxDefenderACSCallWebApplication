﻿using Azure;
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

var key = builder.Configuration.GetValue<string>("AzureOpenAIServiceKey");
ArgumentNullException.ThrowIfNullOrEmpty(key);

var endpoint = builder.Configuration.GetValue<string>("AzureOpenAIServiceEndpoint");
ArgumentNullException.ThrowIfNullOrEmpty(endpoint);

var ai_client = new OpenAI.OpenAIClient(new AzureKeyCredential(key));

//Register and make CallAutomationClient accessible via dependency injection
builder.Services.AddSingleton(client);
builder.Services.AddSingleton(ai_client);
var app = builder.Build();

var devTunnelUri = builder.Configuration.GetValue<string>("DevTunnelUri");
ArgumentNullException.ThrowIfNullOrEmpty(devTunnelUri);
var maxTimeout = 2;

app.MapGet("/", () => "Welcome to Bronx Defenders IVR System!");

// Dataverse related variables
var dataverseUri = builder.Configuration.GetValue<string>("DataverseUri");
var dataverseClientId = builder.Configuration.GetValue<string>("DataverseClientId");
var dataverseClientSecret = builder.Configuration.GetValue<string>("DataverseClientSecret");
var dataverseTenantId = builder.Configuration.GetValue<string>("DataverseTenantId");
var dataverseConnectionString = builder.Configuration.GetValue<string>("DataverseConnectionString");

 var confidentialClient = ConfidentialClientApplicationBuilder.Create(dataverseClientId)
            .WithClientSecret(dataverseClientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{dataverseTenantId}"))
            .Build();

var authResult = confidentialClient.AcquireTokenForClient(new[] { $"{dataverseUri}/.default" }).ExecuteAsync().Result;
var accessToken = authResult.AccessToken;

ILogger<ServiceClient> serviceClientLogger = app.Services.GetRequiredService<ILogger<ServiceClient>>();
ServiceClient _serviceClient = new ServiceClient(dataverseConnectionString);

var columnSet = new ColumnSet(true); // Retrieve all columns
var scheduleCollection = _serviceClient.RetrieveMultipleAsync(new QueryExpression("bxd_schedule")
{
    ColumnSet = columnSet
}).Result;

var agentCollection = _serviceClient.RetrieveMultipleAsync(new QueryExpression("bxd_agent")
{
    ColumnSet = columnSet
}).Result;

var skillsCollection = _serviceClient.RetrieveMultipleAsync(new QueryExpression("bxd_skills")
{
    ColumnSet = columnSet
}).Result;

app.MapPost("/api/incomingCall", async (
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

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var callerId = Helper.GetCallerId(jsonObject);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        logger.LogInformation($"Callback Url: {callbackUri}");
        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            CallIntelligenceOptions = new CallIntelligenceOptions() { CognitiveServicesEndpoint = new Uri(cognitiveServicesEndpoint) }
        };

        AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);
        logger.LogInformation($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");

        //Use EventProcessor to process CallConnected event
        var answer_result = await answerCallResult.WaitForEventProcessorAsync();
        if (answer_result.IsSuccess)
        {
            logger.LogInformation($"Call connected event received for connection id: {answer_result.SuccessResult.CallConnectionId}");
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();
            await HandlePlayAsync(helloPrompt, "Hello Prompt", callConnectionMedia);
            await HandleRecognizeAsync(callConnectionMedia, callerId, firstPrompt);

            // Start continuous DTMF recognition
            await StartContinousDTMFRecognition(client, answerCallResult.CallConnection.CallConnectionId, callerId);
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
                await HandleRecognizeAsync(callConnectionMedia, callerId, callerIssuePrompt);
            }
            else if (dtmfEvent.Tone.Equals(DtmfTone.Two) && dtmfEvent.SequenceId == 1)
            {
                await HandleRecognizeAsync(callConnectionMedia, callerId, callerIssuePromptSpanish);
            }
            if (dtmfEvent.Tone.Equals(DtmfTone.One) && dtmfEvent.SequenceId == 2)
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
            else if (dtmfEvent.Tone.Equals(DtmfTone.Two) && dtmfEvent.SequenceId == 2)
            {
                if(string.IsNullOrWhiteSpace(agentPhonenumber))
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
            else if (dtmfEvent.Tone.Equals(DtmfTone.Three) && dtmfEvent.SequenceId == 2)
            {
                await HandlePlayAsync(criminalVoicemail, criminalVoiceMailContext, callConnectionMedia);
                Thread.Sleep(3000);
                // Beep sound
                var beepAudioFile = "https://voicemailrecordingstgacc.blob.core.windows.net/bronxdefendersvoicemails/audiofiles/beep.wav";
                await HandlePlayAudioAsync(beepAudioFile, criminalVoiceMailContext, callConnectionMedia);

                //Transfer to voicemail
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
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<PlayFailed>(answerCallResult.CallConnection.CallConnectionId, async (playFailedEvent) =>
        {
            logger.LogInformation($"Play failed event received for connection id: {playFailedEvent.CallConnectionId}. Hanging up call...");
            await answerCallResult.CallConnection.HangUpAsync(true);
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferAccepted>(answerCallResult.CallConnection.CallConnectionId, async (callTransferAcceptedEvent) =>
        {
            logger.LogInformation($"Call transfer accepted event received for connection id: {callTransferAcceptedEvent.CallConnectionId}.");
        });
        // Try different phone number instead of same one.
        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferFailed>(answerCallResult.CallConnection.CallConnectionId, async (callTransferFailedEvent) =>
        {
            logger.LogInformation($"Call transfer failed event received for connection id: {callTransferFailedEvent.CallConnectionId}.");

            var resultInformation = callTransferFailedEvent.ResultInformation;
            logger.LogError("Encountered error during call transfer, message={msg}, code={code}, subCode={subCode}", resultInformation?.Message, resultInformation?.Code, resultInformation?.SubCode);

            // Transfer to voicemail
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
                logger.LogInformation($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Playing goodbye message...");
                await HandlePlayAsync(goodbyePrompt, goodbyeContext, callConnectionMedia);
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

            logger.LogInformation($"Recording Location: {voiceMailRecordingContentLocation}\n Recording Metadata: {voiceMailRecordingMetadataLocation}");
        }
    }
    return Results.Ok($"Recording Download Location : {voiceMailRecordingContentLocation}, Recording Delete Location: {voiceMailRecordingDeleteLocation}");
});

app.MapGet("/api/downloadVoicemail", async (
    ILogger<Program> logger) =>
{
    var callRecording = client.GetCallRecording();
    callRecording.DownloadTo(new Uri(voiceMailRecordingContentLocation), "Recording_File.wav");
    return Results.Ok();
});

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

app.Run();