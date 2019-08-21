// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Linq;
using MixedRealityExtension.App;
using MixedRealityExtension.Messaging.Payloads;
using Newtonsoft.Json;
using UnityEngine;

namespace MixedRealityExtension.RPC
{
    /// <summary>
    /// Class that represents the remote procedure call interface for the MRE interop library.
    /// </summary>
    public sealed class RPCInterface
    {
        private readonly MixedRealityExtensionApp _app;

        private Dictionary<string, RPCHandlerBase> _handlers = new Dictionary<string, RPCHandlerBase>();

        internal RPCInterface(MixedRealityExtensionApp app) => _app = app;

        public enum ClientToClientRpc
        {
            Transform,
            PlacementAck,
            JsonMessage
        }

        /// <summary>
        /// Registers and RPC handler for the specific procedure name
        /// </summary>
        /// <param name="procName">The name of the remote procedure.</param>
        /// <param name="handler">The handler to be called when an RPC call is received for the given procedure name.</param>
        public void OnReceive(string procName, RPCHandlerBase handler)
        {
            _handlers[procName] = handler;
        }

        internal void ReceiveRPC(TransformPayload payload)
        {
            if (_handlers.ContainsKey("transform-payload"))
            {
                _handlers["transform-payload"].Execute(new Newtonsoft.Json.Linq.JToken[] {
                    payload.userId,
                    payload.timeStampId,
                    payload.position[0],
                    payload.position[1],
                    payload.position[2],
                    payload.rotation[0],
                    payload.rotation[1],
                    payload.rotation[2],
                    payload.rotation[3]
                    });
            }
        }

        internal void ReceiveRPC(AckPayload payload)
        {
            if (_handlers.ContainsKey("ack-payload"))
            {
                _handlers["ack-payload"].Execute(new Newtonsoft.Json.Linq.JToken[] {
                payload.userId,
                payload.timeStampId
                });
            }
        }

        internal void ReceiveRPC(JsonMessagePayload payload)
        {
            if (_handlers.ContainsKey("json-message-payload"))
            {
                _handlers["json-message-payload"].Execute(new Newtonsoft.Json.Linq.JToken[] {
                    payload.userId,
                    payload.payloadType,
                    payload.jsonBody
                    });
            }
        }

        internal void ReceiveRPC(AppToEngineRPC payload)
        {
            if (_handlers.ContainsKey(payload.ProcName))
            {
                _handlers[payload.ProcName].Execute(payload.Args.Children().ToArray());
            }
        }

        /// <summary>
        /// Sends an RPC message to the app with the given name and arguments.
        /// </summary>
        /// <param name="procName">The name of the remote procedure call.</param>
        /// <param name="args">The arguments for the remote procedure call.</param>
        public void SendRPC(string procName, params object[] args)
        {
            _app.Protocol.Send(new EngineToAppRPC()
            {
                ProcName = procName,
                Args = args.ToList()
            });
        }

        public void SendRPC(ClientToClientRpc type, params object[] args)
        {
            switch(type)
            {
                case ClientToClientRpc.Transform:
                    Vector3 pos = (Vector3)args[2];
                    Quaternion rot = (Quaternion)args[3];
                    float[] arr1 = new float[] {pos[0], pos[1], pos[2]};
                    float[] arr2 = new float[] {rot.x, rot.y, rot.z, rot.w};
                    _app.Protocol.Send(new TransformPayload()
                    {
                        userId = (string)args[0],
                        timeStampId = (int)args[1],
                        position = arr1,
                        rotation = arr2
                    });
                    break;

                case ClientToClientRpc.PlacementAck:
                    _app.Protocol.Send(new AckPayload()
                    {
                        userId = (Guid)args[0],
                        timeStampId = (int)args[1]
                    });
                    break;

                case ClientToClientRpc.JsonMessage:
                    _app.Protocol.Send(new JsonMessagePayload()
                    {
                        userId = (Guid)args[0],
                        payloadType = (string)args[1],
                        jsonBody = (string)args[2]
                    });
                    break;

                default:
                    break;
            }
        }
    }
}
