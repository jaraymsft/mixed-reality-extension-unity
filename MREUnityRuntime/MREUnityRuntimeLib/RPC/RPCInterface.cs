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
            Test,
            Timer,
            PlacementAck
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

        internal void ReceiveRPC(TestPayload payload)
        {
            if (_handlers.ContainsKey("test-payload"))
            {
                _handlers["test-payload"].Execute(new Newtonsoft.Json.Linq.JToken[] {
                    payload.userId,
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

        internal void ReceiveRPC(TimerPayload payload)
        {
            if (_handlers.ContainsKey("timer-payload"))
            {
                _handlers["timer-payload"].Execute(new Newtonsoft.Json.Linq.JToken[] { payload.userId, payload.millis });
            }
        }

        internal void ReceiveRPC(AckPayload payload)
        {
            if (_handlers.ContainsKey("ack-payload"))
            {
                _handlers["ack-payload"].Execute(new Newtonsoft.Json.Linq.JToken[] {
                payload.userId,
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
            if (procName == "ack-payload")
            {
                _app.Protocol.Send(new AckPayload()
                {
                    userId = (Guid)args[0]
                });
            }
            else
            {
                _app.Protocol.Send(new EngineToAppRPC()
                {
                    ProcName = procName,
                    Args = args.ToList()
                });
            }
        }

        public void SendRPC(ClientToClientRpc type, params object[] args)
        {
            switch(type)
            {
                case ClientToClientRpc.Test:
                    Vector3 vect1 = (Vector3)args[1];
                    Quaternion vect2 = (Quaternion)args[2];
                    float[] arr1 = new float[] {vect1[0], vect1[1], vect1[2]};
                    float[] arr2 = new float[] {vect2.x, vect2.y, vect2.z, vect2.w};
                    _app.Protocol.Send(new TestPayload()
                    {
                        userId = (Guid)args[0],
                        position = arr1,
                        rotation = arr2
                    });
                    break;

                case ClientToClientRpc.PlacementAck:
                    _app.Protocol.Send(new AckPayload()
                    {
                        userId = (Guid)args[0]
                    });
                    break;

                default:
                    break;
            }

            
            // _app.Protocol.Send(new TimerPayload()
            // {
            //     userId = (Guid)args[0],
            //     millis = (int)args[1]
            // });
        }
    }
}
