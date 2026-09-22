using System;
using NUnit.Framework;
using Gaussians.FourD.Editor;
using UnityEditor;
using UnityEngine;

namespace Gaussians.FourD.Tests
{
    public sealed class Gaussian4DPlaybackTests
    {
        GameObject m_Object;
        GaussianSplat4D m_Player;
        [SetUp] public void SetUp()
        {
            m_Object = new GameObject("4D playback test");
            m_Player = m_Object.AddComponent<GaussianSplat4D>();
            m_Player.m_DurationSeconds = 2;
            m_Player.m_ModelTimeRange = new Vector2(-2,4);
        }
        [TearDown] public void TearDown() => UnityEngine.Object.DestroyImmediate(m_Object);

        [Test] public void SeekMapsPlayerDurationAndClampsWithoutPlaying()
        {
            m_Player.SeekSeconds(1);
            Assert.That(m_Player.m_ModelTime,Is.EqualTo(1));
            Assert.That(m_Player.IsPlaying,Is.False);
            m_Player.SeekNormalized(3);
            Assert.That(m_Player.m_ModelTime,Is.EqualTo(4));
            m_Player.SeekSeconds(-1);
            Assert.That(m_Player.m_ModelTime,Is.EqualTo(-2));
        }
        [Test] public void PauseResumeAndStopHavePublicTransportSemantics()
        {
            Assert.That(m_Player.Play(),Is.True);
            m_Player.Advance(0.5);
            m_Player.Pause(); m_Player.Advance(1);
            Assert.That(m_Player.PositionSeconds,Is.EqualTo(0.5));
            m_Player.Play(); m_Player.Advance(0.25);
            Assert.That(m_Player.PositionSeconds,Is.EqualTo(0.75));
            m_Player.Stop();
            Assert.That(m_Player.PositionSeconds,Is.Zero);
            Assert.That(m_Player.IsPlaying,Is.False);
            Assert.That(m_Player.m_ModelTime,Is.EqualTo(-2));
        }
        [TestCase(1,5,1)]
        [TestCase(1,2,0)]
        [TestCase(-1,5,1)]
        [TestCase(-1,2,0)]
        public void LoopHandlesMultipleCyclesAndBothDirections(float speed,double elapsed,double expected)
        {
            m_Player.m_Speed = speed; m_Player.Play(); m_Player.Advance(elapsed);
            Assert.That(m_Player.PositionSeconds,Is.EqualTo(expected));
            Assert.That(m_Player.IsPlaying,Is.True);
        }
        [TestCase(1,2)]
        [TestCase(-1,0)]
        public void NonLoopingStopsAtEndpointAndPlayRestarts(float speed,double endpoint)
        {
            m_Player.m_Loop=false; m_Player.m_Speed=speed;
            m_Player.Play(); m_Player.Advance(10);
            Assert.That(m_Player.PositionSeconds,Is.EqualTo(endpoint));
            Assert.That(m_Player.IsPlaying,Is.False);
            m_Player.Play();
            Assert.That(m_Player.PositionSeconds,Is.EqualTo(speed>0?0:2));
        }
        [Test] public void DescendingRangeAndZeroSpeedPreserveEndpoint()
        {
            m_Player.m_ModelTimeRange=new Vector2(10,-10);
            m_Player.SeekNormalized(0.25);
            Assert.That(m_Player.m_ModelTime,Is.EqualTo(5));
            m_Player.SeekNormalized(1); m_Player.m_Speed=0;
            m_Player.Play(); m_Player.Advance(20);
            Assert.That(m_Player.m_ModelTime,Is.EqualTo(-10));
        }
        [Test] public void InvalidSettingsNeverWriteNonfiniteRendererTime()
        {
            m_Player.SeekSeconds(1);
            m_Player.Play(); m_Player.m_DurationSeconds=float.NaN; m_Player.Advance(1);
            Assert.That(m_Player.IsPlaying,Is.False);
            Assert.That(m_Player.SeekNormalized(0.5),Is.False);
            Assert.That(m_Player.Play(),Is.False);
            Assert.That(m_Player.m_ModelTime,Is.EqualTo(1));
            m_Player.m_DurationSeconds=2; m_Player.m_ModelTimeRange=new Vector2(0,float.PositiveInfinity);
            Assert.That(m_Player.Play(),Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(()=>m_Player.SeekSeconds(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(()=>m_Player.Advance(-1));
        }
        [Test] public void DisablePausesAndSeekingWhilePlayingKeepsTransportState()
        {
            m_Player.Play(); m_Player.SeekSeconds(1);
            Assert.That(m_Player.IsPlaying,Is.True);
            m_Player.enabled=false;
            Assert.That(m_Player.IsPlaying,Is.False);
            Assert.That(m_Player.Play(),Is.False);
        }

        static void InvokeInspector(UnityEditor.Editor editor,string method,params object[] arguments) =>
            editor.GetType().GetMethod(method,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(editor,arguments);

        [Test] public void EditorPreviewHasSingleOwnerAndRestoresOnClose()
        {
            m_Player.m_ModelTime=0.7f;
            var first=UnityEditor.Editor.CreateEditor(m_Player,typeof(GaussianSplat4DEditor));
            var second=UnityEditor.Editor.CreateEditor(m_Player,typeof(GaussianSplat4DEditor));
            try
            {
                InvokeInspector(first,"StartPlayback");
                m_Player.Advance(0.3);
                InvokeInspector(second,"StartPlayback");
                UnityEngine.Object.DestroyImmediate(first);
                Assert.That(m_Player.IsPlaying,Is.True,"Closing former owner must not stop the new preview");
                UnityEngine.Object.DestroyImmediate(second);
                Assert.That(m_Player.IsPlaying,Is.False);
                Assert.That(m_Player.PositionSeconds,Is.Zero);
                Assert.That(m_Player.m_ModelTime,Is.EqualTo(0.7f));
            }
            finally { if(first)UnityEngine.Object.DestroyImmediate(first); if(second)UnityEngine.Object.DestroyImmediate(second); }
        }

        [Test] public void InspectorOnlyAppliesTimingEditsAndRecordsRendererUndo()
        {
            var editor=UnityEditor.Editor.CreateEditor(m_Player,typeof(GaussianSplat4DEditor));
            try
            {
                m_Player.m_ModelTime=0.7f;
                m_Player.m_Loop=false;
                InvokeInspector(editor,"ApplyTimingChange",2f,m_Player.m_ModelTimeRange);
                Assert.That(m_Player.m_ModelTime,Is.EqualTo(0.7f));
                Undo.IncrementCurrentGroup();
                m_Player.m_DurationSeconds=4;
                InvokeInspector(editor,"ApplyTimingChange",2f,m_Player.m_ModelTimeRange);
                Assert.That(m_Player.m_ModelTime,Is.EqualTo(-2));
                Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                Assert.That(m_Player.m_ModelTime,Is.EqualTo(0.7f));
            }
            finally { Undo.ClearUndo(m_Player); UnityEngine.Object.DestroyImmediate(editor); }
        }
    }
}
