﻿/*
 * Copyright 2018, by the California Institute of Technology. ALL RIGHTS 
 * RESERVED. United States Government Sponsorship acknowledged. Any 
 * commercial use must be negotiated with the Office of Technology 
 * Transfer at the California Institute of Technology.
 * 
 * This software may be subject to U.S.export control laws.By accepting 
 * this software, the user agrees to comply with all applicable 
 * U.S.export laws and regulations. User has the responsibility to 
 * obtain export licenses, or other export authority as may be required 
 * before exporting such information to foreign countries or providing 
 * access to foreign persons.
 */

using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using RSG;
using UnityGLTF.Loader;
using UnityGLTF.Extensions;

namespace Unity3DTiles
{
    /// <summary>
    /// A 3D Tiles tileset used for streaming large 3D datasets
    /// See https://github.com/AnalyticalGraphicsInc/cesium/blob/master/Source/Scene/Cesium3DTileset.js
    /// </summary>
    public class Unity3DTileset
    {
        public Unity3DTilesetOptions TilesetOptions { private set; get; }
        public Unity3DTile Root { get; private set; }

        private string basePath;
        private string tilesetUrl;
        private int previousTilesRemaining = 0;

        private Schema.Tileset tileset;
        public Queue<Unity3DTile> ProcessingQueue;           // Tiles whose content is being loaded/processed

        public MonoBehaviour Behaviour { get; private set; }

        /// <summary>
        /// Maintians a least recently used list of tiles that have content
        /// </summary>
        public LRUCache<Unity3DTile> LRUContent { get; private set; }

        /// <summary>
        /// The deepest depth of the tree as specified by the loaded json structure.  This may increase as recursive json
        /// tilesets are loaded.  This value does not depend on what renderable content has been loaded.
        /// </summary>
        public int DeepestDepth { get; private set; }

        public Unity3DTilesetStatistics Statistics = new Unity3DTilesetStatistics();

        public Unity3DTilesetTraversal Traversal;
        private Promise<Unity3DTileset> readyPromise = new Promise<Unity3DTileset>();

        public delegate void LoadProgressDelegate(int tilesRemaining);
        public event LoadProgressDelegate LoadProgress;

        public delegate void AllTilesLoadedDelegate(bool loaded);
        public event AllTilesLoadedDelegate AllTilesLoaded;

        public delegate void TileDelegate(Unity3DTile tile);

        public bool Ready { get { return this.Root != null; } }

        public RequestManager RequestManager { get; private set;}

        private DateTime loadTimestamp;

        /// <summary>
        /// Time since tileset was loaded in seconds
        /// </summary>
        public double TimeSinceLoad
        {
            get
            {
                return Math.Max((DateTime.UtcNow - this.loadTimestamp).TotalSeconds, 0);
            }
        }

        public void GetRootTransform(out Vector3 translation, out Quaternion rotation, out Vector3 scale,
                                     bool convertToUnityFrame = true)
        {
            var m = Matrix4x4.TRS(TilesetOptions.Translation, TilesetOptions.Rotation, TilesetOptions.Scale);
            if (convertToUnityFrame)
            {
                m = m.UnityMatrix4x4ConvertFromGLTF();
            }
            translation = new Vector3(m.m03, m.m13, m.m23);
            rotation = m.rotation;
            scale = m.lossyScale;
        }

        public Matrix4x4 GetRootTransform(bool convertToUnityFrame = true)
        {
            GetRootTransform(out Vector3 translation, out Quaternion rotation, out Vector3 scale, convertToUnityFrame);
            return Matrix4x4.TRS(translation, rotation, scale);
        }

        public Unity3DTileset(Unity3DTilesetOptions tilesetOptions, AbstractTilesetBehaviour behaviour)
        {
            this.TilesetOptions = tilesetOptions;
            this.Behaviour = behaviour;
            this.RequestManager = behaviour.RequestManager;
            this.ProcessingQueue = behaviour.ProcessingQueue;
            this.LRUContent = behaviour.LRUCache;
            this.Traversal = new Unity3DTilesetTraversal(this, behaviour.SceneOptions);
            this.DeepestDepth = 0;

            string url = UrlUtils.ReplaceDataProtocol(tilesetOptions.Url);

            if (UrlUtils.GetLastPathSegment(url).EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                this.basePath = UrlUtils.GetBaseUri(url);
                this.tilesetUrl = url;
            }
            else
            {
                this.basePath = url;
                this.tilesetUrl = UrlUtils.JoinUrls(url, "tileset.json");
            }

            LoadTilesetJson(this.tilesetUrl).Then(json =>
            {
                // Load Tileset (main tileset or a reference tileset)
                this.tileset = Schema.Tileset.FromJson(json);
                this.Root = LoadTileset(this.tilesetUrl, this.tileset, null);
                this.readyPromise.Resolve(this);
            }).Catch(error =>
            {
                Debug.LogError(error.Message + "\n" + error.StackTrace);
            });
        }

        public Promise<string> LoadTilesetJson(string url)
        {
            Promise<string> promise = new Promise<string>();
            this.Behaviour.StartCoroutine(DownloadTilesetJson(url, promise));
            return promise;
        }

        IEnumerator DownloadTilesetJson(string url, Promise<string> promise)
        {
            Action<string, string> onDownload = new Action<string, string>((text, error) =>
            {
                if (error == null || error == "")
                {
                    promise.Resolve(text);
                    
                }
                else
                {
                    promise.Reject(new System.Exception("Error downloading " + url + " " + error));
                }
            });

            UnityWebRequestLoader wrapper = new UnityWebRequestLoader("");
            yield return wrapper.Send(url, "", onDownloadString: onDownload);
        }

        private Unity3DTile LoadTileset(string tilesetUrl, Schema.Tileset tileset, Unity3DTile parentTile)
        {
            if (tileset.Asset == null)
            {
                Debug.LogError("Tileset must have an asset property");
                return null;
            }
            if (tileset.Asset.Version != "0.0" && tileset.Asset.Version != "1.0")
            {
                Debug.LogError("Tileset must be 3D Tiles version 0.0 or 1.0");
                return null;
            }
            // Add tileset version to base path
            bool hasVersionQuery = new Regex(@"/[?&]v=/").IsMatch(tilesetUrl);
            if (!hasVersionQuery && !new Uri(tilesetUrl).IsFile)
            {
                string version = "0.0";
                if (tileset.Asset.TilesetVersion != null)
                {
                    version = tileset.Asset.TilesetVersion;
                }
                string versionQuery = "v=" + version;
                
                this.basePath = UrlUtils.SetQuery(this.basePath, versionQuery);
                tilesetUrl = UrlUtils.SetQuery(tilesetUrl, versionQuery);
            }
            // A tileset.json referenced from a tile may exist in a different directory than the root tileset.
            // Get the basePath relative to the external tileset.
            string basePath = UrlUtils.GetBaseUri(tilesetUrl);
            Unity3DTile rootTile = new Unity3DTile(this, basePath, tileset.Root, parentTile);
            Statistics.NumberOfTilesTotal++;

            // Loop through the Tile json data and create a tree of Unity3DTiles
            Stack<Unity3DTile> stack = new Stack<Unity3DTile>();
            stack.Push(rootTile);
            while (stack.Count > 0)
            {
                Unity3DTile tile3D = stack.Pop();
                for (int i = 0; i < tile3D.tile.Children.Count; i++)
                {
                    Unity3DTile child = new Unity3DTile(this, basePath, tile3D.tile.Children[i], tile3D);
                    this.DeepestDepth = Math.Max(child.Depth, this.DeepestDepth);
                    Statistics.NumberOfTilesTotal++;
                    stack.Push(child);
                }
                // TODO consider using CullWithChildrenBounds optimization here
            }
            this.loadTimestamp = DateTime.UtcNow;
            return rootTile;
        }

        public void Update()
        {
            if(!this.Ready)
            {
                return;
            }            
            Statistics.Clear();
            Traversal.Run();
            Statistics.RequestQueueLength = this.RequestManager.QueueSize();
            Statistics.ConcurrentRequests = this.RequestManager.RequestsInProgress();
            Statistics.ProcessingTiles = this.ProcessingQueue.Count;
            int remaining = Statistics.RequestQueueLength + Statistics.ConcurrentRequests + Statistics.ProcessingTiles;
            Statistics.TilesLeftToLoad = remaining;
            if (AllTilesLoaded != null)
            {
                if (previousTilesRemaining != 0 && remaining == 0)
                {
                    AllTilesLoaded(true);
                }
                if(previousTilesRemaining == 0 && remaining != 0)
                {
                    AllTilesLoaded(false);
                }
            }
            if (LoadProgress != null && remaining != previousTilesRemaining)
            {
                LoadProgress(remaining);
            }
            previousTilesRemaining = remaining;
            if (this.TilesetOptions.DebugDrawBounds)
            {
                Traversal.DrawDebug();
            }
        }
    }
}
