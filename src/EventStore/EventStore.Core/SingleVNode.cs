﻿// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System.IO;
using System.Net;
using EventStore.Core.Bus;
using EventStore.Core.DataStructures;
using EventStore.Core.Index;
using EventStore.Core.Index.Hashes;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.Monitoring;
using EventStore.Core.Services.RequestManager;
using EventStore.Core.Services.Storage;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Services.Transport.Http;
using EventStore.Core.Services.Transport.Http.Controllers;
using EventStore.Core.Services.Transport.Tcp;
using EventStore.Core.Services.VNode;
using EventStore.Common.Utils;
using EventStore.Core.Settings;
using EventStore.Core.TransactionLog.Chunks;

namespace EventStore.Core
{
    public class SingleVNode
    {
        public QueuedHandler MainQueue { get { return _mainQueue; } }
        public InMemoryBus Bus { get { return _outputBus; } } 
        public HttpService HttpService { get { return _httpService; } }
        public TimerService TimerService { get { return _timerService; } }
        public IPublisher NetworkSendService { get { return _networkSendService; } }

        private readonly IPEndPoint _tcpEndPoint;
        private readonly IPEndPoint _httpEndPoint;

        private readonly QueuedHandler _mainQueue;
        private readonly InMemoryBus _outputBus;

        private readonly SingleVNodeController _controller;
        private readonly HttpService _httpService;
        private readonly TimerService _timerService;
        private readonly NetworkSendService _networkSendService;

        public SingleVNode(TFChunkDb db, 
                           SingleVNodeSettings vNodeSettings, 
                           SingleVNodeAppSettings appSettings, 
                           bool dbVerifyHashes,
                           int memTableEntryCount = TFConsts.MemTableEntryCount)
        {
            Ensure.NotNull(db, "db");
            Ensure.NotNull(vNodeSettings, "vNodeSettings");

            db.OpenVerifyAndClean(dbVerifyHashes);

            _tcpEndPoint = vNodeSettings.ExternalTcpEndPoint;
            _httpEndPoint = vNodeSettings.ExternalHttpEndPoint;

            _outputBus = new InMemoryBus("OutputBus");
            _controller = new SingleVNodeController(Bus, _httpEndPoint);
            _mainQueue = new QueuedHandler(_controller, "MainQueue");
            _controller.SetMainQueue(MainQueue);

            //MONITORING
            var monitoringInnerBus = new InMemoryBus("MonitoringInnerBus", watchSlowMsg: false);
            var monitoringRequestBus = new InMemoryBus("MonitoringRequestBus", watchSlowMsg: false);
            var monitoringQueue = new QueuedHandler(monitoringInnerBus, "MonitoringQueue", watchSlowMsg: true, slowMsgThresholdMs: 100);
            var monitoring = new MonitoringService(monitoringQueue, 
                                                   monitoringRequestBus, 
                                                   MainQueue, 
                                                   db.Config.WriterCheckpoint, 
                                                   db.Config.Path, 
                                                   appSettings.StatsPeriod, 
                                                   _httpEndPoint,
                                                   appSettings.StatsStorage);
            Bus.Subscribe(monitoringQueue.WidenFrom<SystemMessage.SystemInit, Message>());
            Bus.Subscribe(monitoringQueue.WidenFrom<SystemMessage.StateChangeMessage, Message>());
            Bus.Subscribe(monitoringQueue.WidenFrom<SystemMessage.BecomeShuttingDown, Message>());
            Bus.Subscribe(monitoringQueue.WidenFrom<ClientMessage.CreateStreamCompleted, Message>());
            monitoringInnerBus.Subscribe<SystemMessage.SystemInit>(monitoring);
            monitoringInnerBus.Subscribe<SystemMessage.StateChangeMessage>(monitoring);
            monitoringInnerBus.Subscribe<SystemMessage.BecomeShuttingDown>(monitoring);
            monitoringInnerBus.Subscribe<ClientMessage.CreateStreamCompleted>(monitoring);
            monitoringInnerBus.Subscribe<MonitoringMessage.GetFreshStats>(monitoring);

            //STORAGE SUBSYSTEM
            var indexPath = Path.Combine(db.Config.Path, "index");
            var tableIndex = new TableIndex(indexPath,
                                            () => new HashListMemTable(maxSize: memTableEntryCount * 2),
                                            maxSizeForMemory: memTableEntryCount,
                                            maxTablesPerLevel: 2);

            var readIndex = new ReadIndex(_mainQueue, 
                                          TFConsts.ReadIndexReaderCount, 
                                          () => new TFChunkSequentialReader(db, db.Config.WriterCheckpoint, 0), 
                                          () => new TFChunkReader(db, db.Config.WriterCheckpoint), 
                                          tableIndex, 
                                          new XXHashUnsafe(),
                                          new LRUCache<string, StreamCacheInfo>(TFConsts.MetadataCacheCapacity));
            var writer = new TFChunkWriter(db);
            var storageWriter = new StorageWriter(_mainQueue, _outputBus, writer, readIndex);
            var storageReader = new StorageReader(_mainQueue, _outputBus, readIndex, TFConsts.StorageReaderHandlerCount, db.Config.WriterCheckpoint);
            monitoringRequestBus.Subscribe<MonitoringMessage.InternalStatsRequest>(storageReader);

            var chaser = new TFChunkChaser(db, db.Config.WriterCheckpoint, db.Config.ChaserCheckpoint);
            var storageChaser = new StorageChaser(_mainQueue, chaser, readIndex, _tcpEndPoint);
            _outputBus.Subscribe<SystemMessage.SystemInit>(storageChaser);
            _outputBus.Subscribe<SystemMessage.SystemStart>(storageChaser);
            _outputBus.Subscribe<SystemMessage.BecomeShuttingDown>(storageChaser);

            var storageScavenger = new StorageScavenger(db, readIndex);
            _outputBus.Subscribe<SystemMessage.ScavengeDatabase>(storageScavenger);

            // NETWORK SEND
            _networkSendService = new NetworkSendService(tcpQueueCount: 1, httpQueueCount: 2);

            //TCP
            var tcpService = new TcpService(MainQueue, _tcpEndPoint, _networkSendService);
            Bus.Subscribe<SystemMessage.SystemInit>(tcpService);
            Bus.Subscribe<SystemMessage.SystemStart>(tcpService);
            Bus.Subscribe<SystemMessage.BecomeShuttingDown>(tcpService);

            //HTTP
            _httpService = new HttpService(ServiceAccessibility.Private, MainQueue, vNodeSettings.HttpPrefixes, 2);
            Bus.Subscribe<SystemMessage.SystemInit>(HttpService);
            Bus.Subscribe<SystemMessage.BecomeShuttingDown>(HttpService);
            Bus.Subscribe<HttpMessage.SendOverHttp>(HttpService);
            Bus.Subscribe<HttpMessage.UpdatePendingRequests>(HttpService);
            HttpService.SetupController(new AdminController(MainQueue));
            HttpService.SetupController(new PingController());
            HttpService.SetupController(new StatController(monitoringQueue, _networkSendService));
            HttpService.SetupController(new ReadEventDataController(MainQueue, _networkSendService));
            HttpService.SetupController(new AtomController(MainQueue, _networkSendService));
            HttpService.SetupController(new WebSiteController(MainQueue));

            //REQUEST MANAGEMENT
            var requestManagement = new RequestManagementService(MainQueue, 1, 1);
            Bus.Subscribe<StorageMessage.CreateStreamRequestCreated>(requestManagement);
            Bus.Subscribe<StorageMessage.WriteRequestCreated>(requestManagement);
            Bus.Subscribe<StorageMessage.TransactionStartRequestCreated>(requestManagement);
            Bus.Subscribe<StorageMessage.TransactionWriteRequestCreated>(requestManagement);
            Bus.Subscribe<StorageMessage.TransactionCommitRequestCreated>(requestManagement);
            Bus.Subscribe<StorageMessage.DeleteStreamRequestCreated>(requestManagement);
            Bus.Subscribe<StorageMessage.RequestCompleted>(requestManagement);
            Bus.Subscribe<StorageMessage.AlreadyCommitted>(requestManagement);
            Bus.Subscribe<StorageMessage.CommitAck>(requestManagement);
            Bus.Subscribe<StorageMessage.PrepareAck>(requestManagement);
            Bus.Subscribe<StorageMessage.WrongExpectedVersion>(requestManagement);
            Bus.Subscribe<StorageMessage.InvalidTransaction>(requestManagement);
            Bus.Subscribe<StorageMessage.StreamDeleted>(requestManagement);
            Bus.Subscribe<StorageMessage.PreparePhaseTimeout>(requestManagement);
            Bus.Subscribe<StorageMessage.CommitPhaseTimeout>(requestManagement);

            var clientService = new ClientService();
            Bus.Subscribe<TcpMessage.ConnectionClosed>(clientService);
            Bus.Subscribe<ClientMessage.SubscribeToStream>(clientService);
            Bus.Subscribe<ClientMessage.UnsubscribeFromStream>(clientService);
            Bus.Subscribe<ClientMessage.SubscribeToAllStreams>(clientService);
            Bus.Subscribe<ClientMessage.UnsubscribeFromAllStreams>(clientService);
            Bus.Subscribe<StorageMessage.EventCommited>(clientService);

            //TIMER
            _timerService = new TimerService(new ThreadBasedScheduler(new RealTimeProvider()));
            Bus.Subscribe<TimerMessage.Schedule>(TimerService);

            monitoringQueue.Start();
            MainQueue.Start();
        }

        public void Start()
        {
            MainQueue.Publish(new SystemMessage.SystemInit());
        }

        public void Stop()
        {
            MainQueue.Publish(new ClientMessage.RequestShutdown(exitProcessOnShutdown: false));
        }

        public override string ToString()
        {
            return string.Format("[{0}, {1}]", _tcpEndPoint, _httpEndPoint);
        }
    }
}
