using Newtonsoft.Json;
using PagarMe.Bifrost.Commands;
using System;
using System.Linq;
using System.Threading.Tasks;
using WebSocketSharp;
using WebSocketSharp.Server;
using log = PagarMe.Generic.Log;

namespace PagarMe.Bifrost.WebSocket
{
    internal class BifrostBehavior : WebSocketBehavior
    {
        private readonly MposBridge mposBridge;

        public BifrostBehavior(MposBridge mposBridge)
        {
            this.mposBridge = mposBridge;
            IgnoreExtensions = true;
        }


        protected override void OnOpen()
        {
            log.Me.Info("Socket Opened");
        }

        protected override void OnClose(CloseEventArgs args)
        {
            log.Me.Info($"Socket Closed: [{args.Code}] {args.Reason}");
        }


        protected override async void OnMessage(MessageEventArgs args)
        {
            var request = new PaymentRequest();

            await log.TryLogOnExceptionAsync(() =>
            {
                request = JsonConvert.DeserializeObject<PaymentRequest>(args.Data, SnakeCase.Settings);
                return handleMessage(request);
            },
            (e) => processError(e, request));
        }

        private void processError(Exception exception, PaymentRequest request)
        {
            log.TryLogOnException(() =>
            {
                var response = request.GenerateResponse();

                response.ResponseType = PaymentResponse.Type.Error;
                response.Error = $"An error has occured with the [{request.RequestType}] request. See the log and contact the support.";

                send(response);
            });
        }

        private async Task handleMessage(PaymentRequest request)
        {
            var context = mposBridge.GetContext(request.ContextId);
            var response = request.GenerateResponse();

            if (context == null)
            {
                var message = "Error on creating context";
                log.Me.Info(message);
                response.ResponseType = PaymentResponse.Type.Error;
                response.Error = message;
            }
            else
            {
                log.Me.Info(request.RequestType);
                await handleRequest(context, request, response);
            }

            send(response);
        }

        private async Task handleRequest(Context context, PaymentRequest request, PaymentResponse response)
        {
            var keepProcessing = verifyContextSequence(context, request, response);

            if (!keepProcessing)
                return;

            switch (request.RequestType)
            {
                case PaymentRequest.Type.ListDevices:
                    await getDeviceList(context, response);
                    break;

                case PaymentRequest.Type.Initialize:
                    await initialize(context, request, response);
                    break;

                case PaymentRequest.Type.Process:
                    await process(context, request, response);
                    break;

                case PaymentRequest.Type.Finish:
                    await finish(context, request, response);
                    break;

                case PaymentRequest.Type.DisplayMessage:
                    await displayMessage(context, request, response);
                    break;

                case PaymentRequest.Type.Status:
                    await status(context, response);
                    break;

                case PaymentRequest.Type.CloseContext:
                    await close(context, request, response);
                    break;

                default:
                    response.ResponseType = PaymentResponse.Type.UnknownCommand;
                    break;
            }
        }

        private Boolean verifyContextSequence(Context context, PaymentRequest request, PaymentResponse response)
        {
            lock (context)
            {
                var canCallAnytime = new[]
                {
                    PaymentRequest.Type.ListDevices,
                    PaymentRequest.Type.DisplayMessage,
                    PaymentRequest.Type.Status,
                    PaymentRequest.Type.UnknownCommand,
                    PaymentRequest.Type.CloseContext,
                };

                if (!canCallAnytime.Contains(request.RequestType))
                {
                    var allowed = context.CurrentOperation.GetNextAllowed();

                    if (!allowed.Contains(request.RequestType))
                    {
                        var allowedText = String.Join(", ", allowed);
                        response.Error = $"Just follow operations allowed: {allowedText}";
                        response.ResponseType = PaymentResponse.Type.Error;
                        return false;
                    }

                    context.CurrentOperation = request.RequestType;
                }

                return true;
            }
        }

        private async Task getDeviceList(Context context, PaymentResponse response)
        {
            response.DeviceList = await context.ListDevices();
            response.ResponseType = PaymentResponse.Type.DevicesListed;
        }

        private async Task initialize(Context context, PaymentRequest request, PaymentResponse response)
        {
            var initialize = request.Initialize;

            var deviceContextName = mposBridge.GetDeviceContextName(initialize.DeviceId);

            if (deviceContextName != null && deviceContextName != request.ContextId)
            {
                response.ResponseType = PaymentResponse.Type.Error;
                response.Error = $"Device already in use by context {deviceContextName}";
                return;
            }

            var initialized = await context.Initialize(initialize, onError);

            if (initialized.HasValue)
            {
                response.ResponseType = initialized.Value
                    ? PaymentResponse.Type.Initialized
                    : PaymentResponse.Type.AlreadyInitialized;
            }
            else
            {
                response.ResponseType = PaymentResponse.Type.Error;
                response.Error = "Could not initialize device. Perhaps it's not the right port.";
            }
        }

        private async Task status(Context context, PaymentResponse response)
        {
            var result = await context.GetStatus();
            response.Status = result;
            response.ResponseType = PaymentResponse.Type.Status;
        }

        private async Task process(Context context, PaymentRequest request, PaymentResponse response)
        {
            var process = await context.ProcessPayment(request.Process);
            response.Process = process.Result;

            if (process.Result.Status == Mpos.PaymentStatus.Accepted)
            {
                response.ResponseType = PaymentResponse.Type.Processed;
            }
            else
            {
                response.ResponseType = PaymentResponse.Type.Error;
                response.Error = $"Transaction {process.Result.Status}";
            }
        }

        private async Task finish(Context context, PaymentRequest request, PaymentResponse response)
        {
            await context.FinishPayment(request.Finish);
            response.ResponseType = PaymentResponse.Type.Finished;
        }

        private async Task displayMessage(Context context, PaymentRequest request, PaymentResponse response)
        {
            await context.DisplayMessage(request.DisplayMessage);
            response.ResponseType = PaymentResponse.Type.MessageDisplayed;
        }

        private async Task close(Context context, PaymentRequest request, PaymentResponse response)
        {
            await mposBridge.KillContext(request.ContextId);
            response.ResponseType = PaymentResponse.Type.ContextClosed;
        }


        protected override void OnError(ErrorEventArgs e)
        {
            log.Me.Error(e.Message);
            log.Me.Error(e.Exception);
            onError(e.Message);
        }

        private void onError(Int32 errorCode)
        {
            onError($"Error: {errorCode}");
        }

        private void onError(String message)
        {
            var response = new PaymentResponse
            {
                ResponseType = PaymentResponse.Type.Error,
                Error = message
            };

            log.Me.Error(message);

            send(response);
        }

        private void send(PaymentResponse response)
        {
            log.Me.Info(response.ResponseType);

            if (State == WebSocketState.Open)
            {
                Send(JsonConvert.SerializeObject(response, SnakeCase.Settings));
            }
            else
            {
                log.Me.Warn($"Could not send response of {response.ResponseType}, websocket connection not opened.");
            }
        }

        
    }
}