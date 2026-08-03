using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Bees.Tests.PlayMode
{
    /// <summary>
    /// Test-owned host for loading Space additively with production bootstrap side
    /// effects suppressed. It creates an isolated Level shell in the real scene and
    /// attaches the production CampaignScenarioDriver.
    /// </summary>
    internal sealed class CampaignScenarioSceneHost
    {
        private const string SceneName = "Space";
        private readonly int _missionId;
        private IDisposable _isolationScope;
        private GameObject _levelObject;
        private Scene _loadedScene;
        private bool _subscribed;

        public bool SceneLoadRequested { get; private set; }
        public object Stage { get; private set; }
        public object Level { get; private set; }
        public object State { get; private set; }
        public object Driver { get; private set; }
        public Scene LoadedScene => _loadedScene;

        public CampaignScenarioSceneHost(int missionId)
        {
            Type catalog = RuntimeAssembly.GetType(
                "Assets.Scripts.Levels.CampaignMissionCatalog");
            object definition = catalog.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { missionId });
            string status = RuntimeAssembly.GetField(definition, "ScenarioStatus").ToString();
            if (status != "Ready")
            {
                throw new InvalidOperationException(
                    $"Campaign mission {missionId} is not enabled for isolated scenes ({status}).");
            }
            _missionId = missionId;
        }

        public IEnumerator Load()
        {
            if (SceneLoadRequested)
            {
                throw new InvalidOperationException("This campaign scene host has already loaded or requested a scene.");
            }
            if (SceneManager.GetSceneByName(SceneName).isLoaded)
            {
                throw new InvalidOperationException("Space is already loaded by another test or host.");
            }

            Type isolation = RuntimeAssembly.GetType(
                "Assets.Scripts.Levels.CampaignScenarioIsolation");
            _isolationScope = (IDisposable)isolation.GetMethod(
                    "Begin", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { _missionId });

            SceneManager.sceneLoaded += OnSceneLoaded;
            _subscribed = true;
            SceneLoadRequested = true;
            AsyncOperation operation = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
            if (operation == null)
            {
                throw new InvalidOperationException("Unity did not create a Space scene load operation.");
            }
            while (!operation.isDone)
            {
                yield return null;
            }
            yield return null;

            if (!_loadedScene.IsValid() || !_loadedScene.isLoaded)
            {
                throw new InvalidOperationException("Space finished loading without a valid scene callback.");
            }
            if (Stage == null)
            {
                throw new InvalidOperationException("The loaded Space scene contains no Stage component.");
            }

            _levelObject = new GameObject($"Campaign scenario Level {_missionId}");
            SceneManager.MoveGameObjectToScene(_levelObject, _loadedScene);
            Level = _levelObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            State = _levelObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            ((Behaviour)Level).enabled = false;
            RuntimeAssembly.SetField(Level, "Stage", Stage);
            RuntimeAssembly.SetField(Level, "State", State);
            RuntimeAssembly.SetField(State, "Level", Level);
            RuntimeAssembly.SetField(State, "Stage", Stage);
            RuntimeAssembly.SetField(Level, "CurrentLevelOptions", Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.LevelOptions"),
                new object[] { _missionId, 1, $"Scenario mission {_missionId}" }));
            RuntimeAssembly.SetField(Stage, "PrimaryLevel", Level);
            Driver = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignScenarioDriver"),
                new[] { Level, (object)_missionId });
        }

        public IEnumerator Unload()
        {
            Unsubscribe();
            if (_levelObject != null)
            {
                UnityEngine.Object.Destroy(_levelObject);
                _levelObject = null;
                yield return null;
            }
            if (_loadedScene.IsValid() && _loadedScene.isLoaded)
            {
                AsyncOperation operation = SceneManager.UnloadSceneAsync(_loadedScene);
                while (operation != null && !operation.isDone)
                {
                    yield return null;
                }
            }

            Stage = null;
            Level = null;
            State = null;
            Driver = null;
            _loadedScene = default;
            _isolationScope?.Dispose();
            _isolationScope = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != SceneName)
            {
                return;
            }
            _loadedScene = scene;
            Type stageType = RuntimeAssembly.GetType("Stage");
            Behaviour[] behaviours = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Behaviour>(true))
                .ToArray();
            foreach (Behaviour behaviour in behaviours)
            {
                if (behaviour != null)
                {
                    behaviour.enabled = false;
                    if (Stage == null && stageType.IsInstanceOfType(behaviour))
                    {
                        Stage = behaviour;
                    }
                }
            }
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _subscribed = false;
        }
    }
}
