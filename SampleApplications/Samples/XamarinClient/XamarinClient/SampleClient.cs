﻿/* ========================================================================
 * Copyright (c) 2005-2013 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 * 
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 * 
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/


using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Client;
using System.Security.Cryptography.X509Certificates;
using Xamarin.Forms;
using System.Net;

namespace XamarinClient
{
    public class SampleClient
    {
        public enum ConnectionStatus
        {
            None,
            NotConnected,
            Connected,
            Error
        }

        public ConnectionStatus connectionStatus;
        public bool haveAppCertificate;
        public Session session;

        private LabelViewModel info;
        private ApplicationConfiguration config;

        public SampleClient (LabelViewModel text)
        {
            connectionStatus = ConnectionStatus.None;
            session = null;
            info = text;
            haveAppCertificate = false;
            config = null;
        }

        public async void CreateCertificate()
        {
            string currentFolder = DependencyService.Get<IPathService>().PublicExternalFolder.ToString();

            config = new ApplicationConfiguration()
            {
                ApplicationName = "UA Core Sample Client",
                ApplicationType = ApplicationType.Client,
                ApplicationUri = "urn:" + Dns.GetHostName() + ":OPCFoundation:CoreSampleClient",

                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = currentFolder + "own",
                        SubjectName = "UA Core Sample Client"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = currentFolder + "trusted",
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = currentFolder + "issuer",
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = currentFolder + "rejected",
                    },
                    NonceLength = 32,
                    AutoAcceptUntrustedCertificates = true
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
            };

            switch (Device.RuntimePlatform)
            {
                case "Android":
                    config.ApplicationName = "OPC UA Xamarin Sample Client Android";
                    break;
                case "UWP":
                    config.ApplicationName = "OPC UA Xamarin Sample Client UWP";
                    break;
                case "iOS":
                    config.ApplicationName = "OPC UA Xamarin Sample Client IOS";
                    break;
            }
      
            await config.Validate(ApplicationType.Client);

            haveAppCertificate = config.SecurityConfiguration.ApplicationCertificate.Certificate != null;

            if (!haveAppCertificate)
            {
                info.LabelText = "    INFO: Creating new application certificate: " + config.ApplicationName;

                X509Certificate2 certificate = CertificateFactory.CreateCertificate(
                    config.SecurityConfiguration.ApplicationCertificate.StoreType,
                    config.SecurityConfiguration.ApplicationCertificate.StorePath,
                    null,
                    config.ApplicationUri,
                    config.ApplicationName,
                    config.SecurityConfiguration.ApplicationCertificate.SubjectName,
                    null,
                    CertificateFactory.defaultKeySize,
                    DateTime.UtcNow - TimeSpan.FromDays(1),
                    CertificateFactory.defaultLifeTime,
                    CertificateFactory.defaultHashSize,
                    false,
                    null,
                    null);

                config.SecurityConfiguration.ApplicationCertificate.Certificate = certificate;
            }

            haveAppCertificate = config.SecurityConfiguration.ApplicationCertificate.Certificate != null;

            if (haveAppCertificate)
            {
                config.ApplicationUri = Utils.GetApplicationUriFromCertificate(config.SecurityConfiguration.ApplicationCertificate.Certificate);

                if (config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
                {
                    config.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(CertificateValidator_CertificateValidation);
                }
            }
        }

        public async Task<ConnectionStatus>OpcClient(string endpointURL)
        {
            try
            {
                Uri endpointURI = new Uri(endpointURL);
                var selectedEndpoint = CoreClientUtils.SelectEndpoint(endpointURL, haveAppCertificate, 15000);

                info.LabelText = "    Selected endpoint uses: " + selectedEndpoint.SecurityPolicyUri.Substring(selectedEndpoint.SecurityPolicyUri.LastIndexOf('#') + 1);

                var endpointConfiguration = EndpointConfiguration.Create(config);
                var endpoint = new ConfiguredEndpoint(selectedEndpoint.Server, endpointConfiguration);
                endpoint.Update(selectedEndpoint);

                var platform = Device.RuntimePlatform;
                var sessionName = "";

                switch (Device.RuntimePlatform)
                {
                    case "Android":
                        sessionName = "OPC UA Xamarin Client Android";
                        break;
                    case "UWP":
                        sessionName = "OPC UA Xamarin Client UWP";
                        break;
                    case "iOS":
                        sessionName = "OPC UA Xamarin Client IOS";
                        break;
                }
                session = await Session.Create(config, endpoint, true, sessionName, 60000, new UserIdentity(new AnonymousIdentityToken()), null);


                if (session != null)
                {
                    connectionStatus = ConnectionStatus.Connected;
                }
                else
                {
                    connectionStatus = ConnectionStatus.NotConnected;
                }
            }
            catch
            {
                connectionStatus = ConnectionStatus.Error;
            }
            return connectionStatus;
        }

        public void Disconnect(Session session)
        {
            if (session != null)
            {
                if (info != null)
                {
                    info.LabelText = "";
                }
                
                session.Close();
            }
        }

        public Tree GetRootNode(LabelViewModel textInfo)
        {
            ReferenceDescriptionCollection references;
            Byte[] continuationPoint;
            Tree browserTree = new Tree();
            
            try
            {
                session.Browse(
                    null,
                    null,
                    ObjectIds.ObjectsFolder,
                    0u,
                    BrowseDirection.Forward,
                    ReferenceTypeIds.HierarchicalReferences,
                    true,
                    0,
                    out continuationPoint,
                    out references);

                browserTree.currentView.Add(new ListNode { id = ObjectIds.ObjectsFolder.ToString(), NodeName = "Root", children = (references?.Count != 0) });

                return browserTree;
            }
            catch (Exception exception)
            {
                Disconnect(session);
                return null;
            }
        }

        public Tree GetChildren(string node)
        {
            ReferenceDescriptionCollection references;
            Byte[] continuationPoint;
            Tree browserTree = new Tree();

            try
            {
                session.Browse(
                    null,
                    null,
                    node,
                    0u,
                    BrowseDirection.Forward,
                    ReferenceTypeIds.HierarchicalReferences,
                    true,
                    0,
                    out continuationPoint,
                    out references);

                if (references != null)
                {
                    foreach (var nodeReference in references)
                    {
                        ReferenceDescriptionCollection childReferences = null;
                        Byte[] childContinuationPoint;

                        session.Browse(
                            null,
                            null,
                            ExpandedNodeId.ToNodeId(nodeReference.NodeId, session.NamespaceUris),
                            0u,
                            BrowseDirection.Forward,
                            ReferenceTypeIds.HierarchicalReferences,
                            true,
                            0,
                            out childContinuationPoint,
                            out childReferences);

                        INode currentNode = null;
                        try
                        {
                            currentNode = session.ReadNode(ExpandedNodeId.ToNodeId(nodeReference.NodeId, session.NamespaceUris));
                        }
                        catch (Exception)
                        {
                            // skip this node
                            continue;
                        }

                        byte currentNodeAccessLevel = 0;
                        byte currentNodeEventNotifier = 0;
                        bool currentNodeExecutable = false;

                        VariableNode variableNode = currentNode as VariableNode;
                        if (variableNode != null)
                        {
                            currentNodeAccessLevel = variableNode.UserAccessLevel;
                            currentNodeAccessLevel = (byte)((uint)currentNodeAccessLevel & ~0x2);
                        }

                        ObjectNode objectNode = currentNode as ObjectNode;
                        if (objectNode != null)
                        {
                            currentNodeEventNotifier = objectNode.EventNotifier;
                        }

                        ViewNode viewNode = currentNode as ViewNode;
                        if (viewNode != null)
                        {
                            currentNodeEventNotifier = viewNode.EventNotifier;
                        }

                        MethodNode methodNode = currentNode as MethodNode;
                        if (methodNode != null)
                        {
                            currentNodeExecutable = methodNode.UserExecutable;
                        }

                        browserTree.currentView.Add(new ListNode()
                        {
                            id = nodeReference.NodeId.ToString(),
                            NodeName = nodeReference.DisplayName.Text.ToString(),
                            nodeClass = nodeReference.NodeClass.ToString(),
                            accessLevel = currentNodeAccessLevel.ToString(),
                            eventNotifier = currentNodeEventNotifier.ToString(),
                            executable = currentNodeExecutable.ToString(),
                            children = (references?.Count != 0),
                            ImageUrl = (nodeReference.NodeClass.ToString() == "Variable") ? "folderOpen.jpg" : "folder.jpg"
                        });
                        if (browserTree.currentView[0].ImageUrl == null)
                        {
                            browserTree.currentView[0].ImageUrl = "";
                        }
                    }
                    if (browserTree.currentView.Count == 0)
                    {
                        INode currentNode = session.ReadNode(new NodeId(node));

                        byte currentNodeAccessLevel = 0;
                        byte currentNodeEventNotifier = 0;
                        bool currentNodeExecutable = false;

                        VariableNode variableNode = currentNode as VariableNode;

                        if (variableNode != null)
                        {
                            currentNodeAccessLevel = variableNode.UserAccessLevel;
                            currentNodeAccessLevel = (byte)((uint)currentNodeAccessLevel & ~0x2);
                        }

                        ObjectNode objectNode = currentNode as ObjectNode;

                        if (objectNode != null)
                        {
                            currentNodeEventNotifier = objectNode.EventNotifier;
                        }

                        ViewNode viewNode = currentNode as ViewNode;

                        if (viewNode != null)
                        {
                            currentNodeEventNotifier = viewNode.EventNotifier;
                        }

                        MethodNode methodNode = currentNode as MethodNode;

                        if (methodNode != null )
                        {
                            currentNodeExecutable = methodNode.UserExecutable;
                        }

                        browserTree.currentView.Add(new ListNode()
                        {
                            id = node,
                            NodeName = currentNode.DisplayName.Text.ToString(),
                            nodeClass = currentNode.NodeClass.ToString(),
                            accessLevel = currentNodeAccessLevel.ToString(),
                            eventNotifier = currentNodeEventNotifier.ToString(),
                            executable = currentNodeExecutable.ToString(),
                            children = false,
                            ImageUrl = null
                        });
                    }
                }
                return browserTree;
            }
            catch (Exception exception)
            {
                Disconnect(session);
                return null;
            }
        }

        public string VariableRead(string node)
        {
            try
            {
                DataValueCollection values = null;
                DiagnosticInfoCollection diagnosticInfos = null;
                ReadValueIdCollection nodesToRead = new ReadValueIdCollection();
                ReadValueId valueId = new ReadValueId();
                valueId.NodeId = new NodeId(node);
                valueId.AttributeId = Attributes.Value;
                valueId.IndexRange = null;
                valueId.DataEncoding = null;
                nodesToRead.Add(valueId);
                ResponseHeader responseHeader = session.Read(null, 0, TimestampsToReturn.Both, nodesToRead, out values, out diagnosticInfos);
                string value = "";
                if (values[0].Value != null)
                {
                    var rawValue  = values[0].WrappedValue.ToString();
                    value = rawValue.Replace("|","\r\n").Replace("{","").Replace("}","");
                }
                return value;
            }
            catch (Exception exception)
            {
                return null;
            }
        }

        private void CertificateValidator_CertificateValidation(CertificateValidator validator, CertificateValidationEventArgs e)
        {
            e.Accept = (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted);
            // This will just accept any certificate
            //e.Accept = true;
        }
    }
}
