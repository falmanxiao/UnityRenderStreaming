﻿using System;
using System.Collections;
using System.Threading;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.RenderStreaming.Signaling;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace Unity.RenderStreaming
{
    [TestFixture(typeof(WebSocketSignaling))]
    [TestFixture(typeof(HttpSignaling))]
    public class PrivateSignalingTest : IPrebuildSetup
    {
        private readonly Type m_SignalingType;
        private Process m_ServerProcess;
        private RTCSessionDescription m_DescOffer;
        private RTCSessionDescription m_DescAnswer;
        private RTCIceCandidate m_candidate;

        private SynchronizationContext m_Context;
        private ISignaling signaling1;
        private ISignaling signaling2;

        public PrivateSignalingTest()
        {
        }

        public PrivateSignalingTest(Type type)
        {
            m_SignalingType = type;
        }

        public void Setup()
        {
#if UNITY_EDITOR
            string dir = System.IO.Directory.GetCurrentDirectory();
            string fileName = System.IO.Path.Combine(dir, Editor.WebAppDownloader.GetFileName());
            if (System.IO.File.Exists(fileName) || System.IO.File.Exists(TestUtility.GetWebAppLocationFromEnv()))
            {
                // already exists.
                return;
            }

            bool downloadRaised = false;
            Editor.WebAppDownloader.DownloadCurrentVersionWebApp(dir, success => { downloadRaised = true; });
            TestUtility.Wait(() => downloadRaised, 10000);
#endif
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            m_ServerProcess = new Process();

            string fileName = TestUtility.GetWebAppLocationFromEnv();

            if (string.IsNullOrEmpty(fileName))
            {
                Debug.Log($"webapp file not found in {fileName}");
                string dir = System.IO.Directory.GetCurrentDirectory();
                fileName = System.IO.Path.Combine(dir, TestUtility.GetFileName());
            }

            Assert.IsTrue(System.IO.File.Exists(fileName), $"webapp file not found in {fileName}");
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = fileName,
                UseShellExecute = false
            };

            string arguments = "-m private";

            if (m_SignalingType == typeof(WebSocketSignaling))
            {
                arguments += " -w";
            }

            startInfo.Arguments = arguments;

            m_ServerProcess.StartInfo = startInfo;
            m_ServerProcess.OutputDataReceived += (sender, e) =>
            {
                Debug.Log(e.Data);
            };
            bool success = m_ServerProcess.Start();
            Assert.True(success);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            m_ServerProcess.Kill();
            m_ServerProcess.WaitForExit();
            m_ServerProcess.Dispose();
            m_ServerProcess = null;
        }

        ISignaling CreateSignaling(Type type, SynchronizationContext mainThread)
        {
            if (type == typeof(WebSocketSignaling))
            {
                return new WebSocketSignaling("ws://localhost", 0.1f, mainThread);
            }

            if (type == typeof(HttpSignaling))
            {
                return new HttpSignaling("http://localhost", 0.1f, mainThread);
            }

            throw new ArgumentException();
        }

        [UnitySetUp, Timeout(1000)]
        public IEnumerator UnitySetUp()
        {
            WebRTC.WebRTC.Initialize();

            RTCConfiguration config = default;
            RTCIceCandidate? candidate_ = null;
            config.iceServers = new[] {new RTCIceServer {urls = new[] {"stun:stun.l.google.com:19302"}}};

            var peer1 = new RTCPeerConnection(ref config);
            var peer2 = new RTCPeerConnection(ref config);
            peer1.OnIceCandidate = candidate => { candidate_ = candidate; };

            MediaStream stream = WebRTC.Audio.CaptureStream();
            peer1.AddTrack(stream.GetTracks().First());

            RTCOfferOptions offerOptions = new RTCOfferOptions();
            var op1 = peer1.CreateOffer(ref offerOptions);
            yield return op1;
            m_DescOffer = op1.Desc;
            var op2 = peer1.SetLocalDescription(ref m_DescOffer);
            yield return op2;
            var op3 = peer2.SetRemoteDescription(ref m_DescOffer);
            yield return op3;

            RTCAnswerOptions answerOptions = new RTCAnswerOptions();
            var op4 = peer2.CreateAnswer(ref answerOptions);
            yield return op4;
            m_DescAnswer = op4.Desc;
            var op5 = peer2.SetLocalDescription(ref m_DescAnswer);
            yield return op5;
            var op6 = peer1.SetRemoteDescription(ref m_DescAnswer);
            yield return op6;

            yield return new WaitUntil(() => candidate_ != null);
            m_candidate = candidate_.Value;

            stream.Dispose();
            peer1.Close();
            peer2.Close();

            m_Context = SynchronizationContext.Current;
            signaling1 = CreateSignaling(m_SignalingType, m_Context);
            signaling2 = CreateSignaling(m_SignalingType, m_Context);
        }

        [TearDown]
        public void TearDown()
        {
            WebRTC.WebRTC.Dispose();
            m_Context = null;
        }

        [UnityTest]
        public IEnumerator CheckPeerExists()
        {
            bool startRaised1 = false;
            bool startRaised2 = false;

            signaling1.OnStart += s => { startRaised1 = true; };
            signaling1.Start();
            signaling2.OnStart += s => { startRaised2 = true; };
            signaling2.Start();

            yield return new WaitUntil(() => startRaised1 && startRaised2);

            const string connectionId = "12345";
            string receiveConnectionId1 = null;
            string receiveConnectionId2 = null;
            bool receivePeerExists1 = false;
            bool receivePeerExists2 = false;

            signaling1.OnCreateConnection += (s, id, peerExists) =>
            {
                receiveConnectionId1 = id;
                receivePeerExists1 = peerExists;
            };
            signaling1.OpenConnection(connectionId);
            yield return new WaitUntil(() => !string.IsNullOrEmpty(receiveConnectionId1));
            Assert.AreEqual(connectionId, receiveConnectionId1);
            Assert.IsFalse(receivePeerExists1);

            signaling2.OnCreateConnection += (s, id, peerExists) =>
            {
                receiveConnectionId2 = id;
                receivePeerExists2 = peerExists;
            };
            signaling2.OpenConnection(connectionId);
            yield return new WaitUntil(() => !string.IsNullOrEmpty(receiveConnectionId2));
            Assert.AreEqual(connectionId, receiveConnectionId2);
            Assert.IsTrue(receivePeerExists2);

            signaling1.CloseConnection(receiveConnectionId1);
            signaling2.CloseConnection(receiveConnectionId2);
            signaling1.Stop();
            signaling2.Stop();
            yield return new WaitForSeconds(1);
        }

        [UnityTest]
        public IEnumerator OnOffer()
        {
            bool startRaised1 = false;
            bool startRaised2 = false;
            bool offerRaised2 = false;
            const string connectionId = "12345";
            string connectionId1 = null;
            string connectionId2 = null;

            signaling1.OnStart += s => { startRaised1 = true; };
            signaling2.OnStart += s => { startRaised2 = true; };
            signaling1.Start();
            signaling2.Start();
            yield return new WaitUntil(() => startRaised1 && startRaised2);

            signaling1.OnCreateConnection += (s, id, peerExists) => { connectionId1 = id; };
            signaling1.OpenConnection(connectionId);
            yield return new WaitUntil(() => !string.IsNullOrEmpty(connectionId1));

            signaling2.OnOffer += (s, e) => { offerRaised2 = true; };

            LogAssert.Expect(LogType.Error, new Regex("."));
            signaling1.SendOffer(connectionId, m_DescOffer);
            yield return new WaitForSeconds(5);
            // Do not receive offer other signaling if not connected same sendoffer connectionId in private mode
            Assert.IsFalse(offerRaised2);

            signaling2.OnCreateConnection += (s, id, peerExists) => { connectionId2 = id; };
            signaling2.OpenConnection(connectionId);
            yield return new WaitUntil(() => !string.IsNullOrEmpty(connectionId2));

            signaling1.SendOffer(connectionId, m_DescOffer);
            yield return new WaitUntil(() => offerRaised2);

            signaling1.CloseConnection(connectionId1);
            signaling2.CloseConnection(connectionId2);
            signaling1.Stop();
            signaling2.Stop();
            yield return new WaitForSeconds(1);
        }


        [UnityTest]
        public IEnumerator OnAnswer()
        {
            bool startRaised1 = false;
            bool startRaised2 = false;
            bool offerRaised = false;
            bool answerRaised = false;
            const string connectionId = "12345";
            string connectionId1 = null;
            string connectionId2 = null;

            signaling1.OnStart += s => { startRaised1 = true; };
            signaling2.OnStart += s => { startRaised2 = true; };
            signaling1.Start();
            signaling2.Start();
            yield return new WaitUntil(() => startRaised1 && startRaised2);

            signaling1.OnCreateConnection += (s, id, peerExists) => { connectionId1 = id; };
            signaling1.OpenConnection(connectionId);
            signaling2.OnCreateConnection += (s, id, peerExists) => { connectionId2 = id; };
            signaling2.OpenConnection(connectionId);
            yield return new WaitUntil(() => !string.IsNullOrEmpty(connectionId1) && !string.IsNullOrEmpty(connectionId2));

            signaling2.OnOffer += (s, e) => { offerRaised = true; };
            signaling1.SendOffer(connectionId1, m_DescOffer);
            yield return new WaitUntil(() => offerRaised);

            signaling1.OnAnswer += (s, e) => { answerRaised = true; };
            signaling2.SendAnswer(connectionId1, m_DescAnswer);
            yield return new WaitUntil(() => answerRaised);

            signaling1.CloseConnection(connectionId1);
            signaling2.CloseConnection(connectionId2);
            signaling1.Stop();
            signaling2.Stop();
            yield return new WaitForSeconds(1);
        }

        [UnityTest]
        public IEnumerator OnCandidate()
        {
            bool startRaised1 = false;
            bool startRaised2 = false;
            bool offerRaised = false;
            bool answerRaised = false;
            bool candidateRaised1 = false;
            bool candidateRaised2 = false;
            const string connectionId = "12345";
            string connectionId1 = null;
            string connectionId2 = null;

            signaling1.OnStart += s => { startRaised1 = true; };
            signaling2.OnStart += s => { startRaised2 = true; };
            signaling1.Start();
            signaling2.Start();
            yield return new WaitUntil(() => startRaised1 && startRaised2);

            signaling1.OnCreateConnection += (s, id, peerExists) => { connectionId1 = id; };
            signaling1.OpenConnection(connectionId);
            signaling2.OnCreateConnection += (s, id, peerExists) => { connectionId2 = id; };
            signaling2.OpenConnection(connectionId);
            yield return new WaitUntil(() => !string.IsNullOrEmpty(connectionId1) && !string.IsNullOrEmpty(connectionId2));

            signaling2.OnOffer += (s, e) => { offerRaised = true; };
            signaling1.SendOffer(connectionId1, m_DescOffer);
            yield return new WaitUntil(() => offerRaised);

            signaling1.OnAnswer += (s, e) => { answerRaised = true; };
            signaling2.SendAnswer(connectionId1, m_DescAnswer);
            yield return new WaitUntil(() => answerRaised);

            signaling2.OnIceCandidate += (s, e) => { candidateRaised1 = true; };
            signaling1.SendCandidate(connectionId1, m_candidate);
            yield return new WaitUntil(() => candidateRaised1);

            signaling1.OnIceCandidate += (s, e) => { candidateRaised2 = true; };
            signaling2.SendCandidate(connectionId1, m_candidate);
            yield return new WaitUntil(() => candidateRaised2);

            signaling1.CloseConnection(connectionId1);
            signaling2.CloseConnection(connectionId2);
            signaling1.Stop();
            signaling2.Stop();
            yield return new WaitForSeconds(1);
        }
    }
}
