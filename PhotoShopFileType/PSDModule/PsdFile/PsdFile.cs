﻿/////////////////////////////////////////////////////////////////////////////////
//
// Photoshop PSD FileType Plugin for Paint.NET
// http://psdplugin.codeplex.com/
//
// This software is provided under the MIT License:
//   Copyright (c) 2006-2007 Frank Blumenberg
//   Copyright (c) 2010-2013 Tao Yue
//
// Portions of this file are provided under the BSD 3-clause License:
//   Copyright (c) 2006, Jonas Beckeman
//
// See LICENSE.txt for complete licensing and attribution information.
//
/////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;


namespace PhotoshopFile {
    public enum PsdColorMode {
        Bitmap = 0,
        Grayscale = 1,
        Indexed = 2,
        RGB = 3,
        CMYK = 4,
        Multichannel = 7,
        Duotone = 8,
        Lab = 9
    };


    public class PsdFile {
        /// <summary>
        /// Represents the composite image.
        /// </summary>
        public Layer BaseLayer { get; set; }

        public ImageCompression ImageCompression { get; set; }

        ///////////////////////////////////////////////////////////////////////////

        public PsdFile() {
            Version = 1;
            BaseLayer = new Layer(this);

            ImageResources = new ImageResources();
            Layers = new List<Layer>();
            AdditionalInfo = new List<LayerInfo>();
        }

        public PsdFile(string filename, Encoding encoding)
            : this() {
            using (var stream = new FileStream(filename, FileMode.Open)) {
                Load(stream, encoding);
            }
        }

        public PsdFile(Stream stream, Encoding encoding)
            : this() {
            Load(stream, encoding);
        }

        ///////////////////////////////////////////////////////////////////////////

        private void Load(Stream stream, Encoding encoding) {
            var reader = new PsdBinaryReader(stream, encoding);

            LoadHeader(reader);
            LoadColorModeData(reader);
            LoadImageResources(reader);
            LoadLayerAndMaskInfo(reader);

            LoadImage(reader);
            DecompressImages();
        }

        public void Save(string fileName, Encoding encoding) {
            using (var stream = new FileStream(fileName, FileMode.Create)) {
                Save(stream, encoding);
            }
        }

        public void Save(Stream stream, Encoding encoding) {
            if (BitDepth != 8)
                throw new NotImplementedException("Only 8-bit color has been implemented for saving.");

            var writer = new PsdBinaryWriter(stream, encoding);
            writer.AutoFlush = true;

            PrepareSave();

            SaveHeader(writer);
            SaveColorModeData(writer);
            SaveImageResources(writer);
            SaveLayerAndMaskInfo(writer);
            SaveImage(writer);
        }

        ///////////////////////////////////////////////////////////////////////////

        #region Header

        /// <summary>
        /// Always equal to 1.
        /// </summary>
        public Int16 Version { get; private set; }

        private Int16 channelCount;
        /// <summary>
        /// The number of channels in the image, including any alpha channels.
        /// </summary>
        public Int16 ChannelCount {
            get { return channelCount; }
            set {
                if (value < 1 || value > 56)
                    throw new ArgumentException("Number of channels must be from 1 to 56.");
                channelCount = value;
            }
        }

        /// <summary>
        /// The height of the image in pixels.
        /// </summary>
        public int RowCount {
            get { return (int)this.BaseLayer.Rect.height; }
            set {
                if (value < 0 || value > 30000)
                    throw new ArgumentException("Number of rows must be from 1 to 30000.");
                BaseLayer.Rect = new Rect(0, 0, BaseLayer.Rect.width, value);
            }
        }


        /// <summary>
        /// The width of the image in pixels. 
        /// </summary>
        public int ColumnCount {
            get { return (int)this.BaseLayer.Rect.width; }
            set {
                if (value < 0 || value > 30000)
                    throw new ArgumentException("Number of columns must be from 1 to 30000.");
                this.BaseLayer.Rect = new Rect(0, 0, value, this.BaseLayer.Rect.height);
            }
        }

        private int bitDepth;
        /// <summary>
        /// The number of bits per channel. Supported values are 1, 8, 16, and 32.
        /// </summary>
        public int BitDepth {
            get { return bitDepth; }
            set {
                switch (value) {
                    case 1:
                    case 8:
                    case 16:
                    case 32:
                        bitDepth = value;
                        break;
                    default:
                        throw new NotImplementedException("Invalid bit depth.");
                }
            }
        }

        /// <summary>
        /// The color mode of the file.
        /// </summary>
        public PsdColorMode ColorMode { get; set; }

        ///////////////////////////////////////////////////////////////////////////

        private void LoadHeader(PsdBinaryReader reader) {
            var signature = reader.ReadAsciiChars(4);
            if (signature != "8BPS")
                throw new PsdInvalidException("The given stream is not a valid PSD file");

            Version = reader.ReadInt16();
            if (Version != 1)
                throw new PsdInvalidException("The PSD file has an unknown version");

            //6 bytes reserved
            reader.BaseStream.Position += 6;

            this.ChannelCount = reader.ReadInt16();
            this.RowCount = reader.ReadInt32();
            this.ColumnCount = reader.ReadInt32();
            BitDepth = reader.ReadInt16();
            ColorMode = (PsdColorMode)reader.ReadInt16();
        }

        ///////////////////////////////////////////////////////////////////////////

        private void SaveHeader(PsdBinaryWriter writer) {
            string signature = "8BPS";
            writer.WriteAsciiChars(signature);
            writer.Write(Version);
            writer.Write(new byte[] { 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, });
            writer.Write(ChannelCount);
            writer.Write(RowCount);
            writer.Write(ColumnCount);
            writer.Write((Int16)BitDepth);
            writer.Write((Int16)ColorMode);
        }

        #endregion

        ///////////////////////////////////////////////////////////////////////////

        #region ColorModeData

        /// <summary>
        /// If ColorMode is ColorModes.Indexed, the following 768 bytes will contain 
        /// a 256-color palette. If the ColorMode is ColorModes.Duotone, the data 
        /// following presumably consists of screen parameters and other related information. 
        /// Unfortunately, it is intentionally not documented by Adobe, and non-Photoshop 
        /// readers are advised to treat duotone images as gray-scale images.
        /// </summary>
        public byte[] ColorModeData = new byte[0];

        private void LoadColorModeData(PsdBinaryReader reader) {
            var paletteLength = reader.ReadUInt32();
            if (paletteLength > 0) {
                ColorModeData = reader.ReadBytes((int)paletteLength);
            }
        }

        private void SaveColorModeData(PsdBinaryWriter writer) {
            writer.Write((UInt32)ColorModeData.Length);
            writer.Write(ColorModeData);
        }

        #endregion

        ///////////////////////////////////////////////////////////////////////////

        #region ImageResources

        /// <summary>
        /// The Image resource blocks for the file
        /// </summary>
        public ImageResources ImageResources { get; set; }

        public ResolutionInfo Resolution {
            get {
                return (ResolutionInfo)ImageResources.Get(ResourceID.ResolutionInfo);
            }

            set {
                ImageResources.Set(value);
            }
        }


        ///////////////////////////////////////////////////////////////////////////

        private void LoadImageResources(PsdBinaryReader reader) {
            var imageResourcesLength = reader.ReadUInt32();
            if (imageResourcesLength <= 0)
                return;

            var startPosition = reader.BaseStream.Position;
            var endPosition = startPosition + imageResourcesLength;
            while (reader.BaseStream.Position < endPosition) {
                var imageResource = ImageResourceFactory.CreateImageResource(reader);
                ImageResources.Add(imageResource);
            }

            //-----------------------------------------------------------------------
            // make sure we are not on a wrong offset, so set the stream position 
            // manually
            reader.BaseStream.Position = startPosition + imageResourcesLength;
        }

        ///////////////////////////////////////////////////////////////////////////

        private void SaveImageResources(PsdBinaryWriter writer) {
            using (new PsdBlockLengthWriter(writer)) {
                foreach (var imgRes in ImageResources)
                    imgRes.Save(writer);
            }
        }

        #endregion

        ///////////////////////////////////////////////////////////////////////////

        #region LayerAndMaskInfo

        public List<Layer> Layers { get; private set; }

        public List<LayerInfo> AdditionalInfo { get; private set; }

        public bool AbsoluteAlpha { get; set; }

        ///////////////////////////////////////////////////////////////////////////

        private void LoadLayerAndMaskInfo(PsdBinaryReader reader) {
            var layersAndMaskLength = reader.ReadUInt32();
            if (layersAndMaskLength <= 0)
                return;

            var startPosition = reader.BaseStream.Position;
            var endPosition = startPosition + layersAndMaskLength;

            LoadLayers(reader, true);
            LoadGlobalLayerMask(reader);

            //-----------------------------------------------------------------------
            // Load Additional Layer Information

            while (reader.BaseStream.Position < endPosition) {
                var info = LayerInfoFactory.Load(reader);
                AdditionalInfo.Add(info);

                if (info is RawLayerInfo) {
                    var layerInfo = (RawLayerInfo)info;
                    switch (info.Key) {
                        case "Layr":
                        case "Lr16":
                        case "Lr32":
                            using (var memoryStream = new MemoryStream(layerInfo.Data))
                            using (var memoryReader = new PsdBinaryReader(memoryStream, reader)) {
                                LoadLayers(memoryReader, false);
                            }
                            break;

                        case "LMsk":
                            GlobalLayerMaskData = layerInfo.Data;
                            break;
                    }
                }
            }

            //-----------------------------------------------------------------------
            // make sure we are not on a wrong offset, so set the stream position 
            // manually
            reader.BaseStream.Position = startPosition + layersAndMaskLength;
        }

        ///////////////////////////////////////////////////////////////////////////

        private void SaveLayerAndMaskInfo(PsdBinaryWriter writer) {
            using (new PsdBlockLengthWriter(writer)) {
                var startPosition = writer.BaseStream.Position;

                SaveLayers(writer);
                SaveGlobalLayerMask(writer);

                foreach (var info in AdditionalInfo)
                    info.Save(writer);

                writer.WritePadding(startPosition, 2);
            }
        }

        ///////////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Load Layers Info section, including image data.
        /// </summary>
        /// <param name="reader">PSD reader.</param>
        /// <param name="hasHeader">Whether the Layers Info section has a length header.</param>
        private void LoadLayers(PsdBinaryReader reader, bool hasHeader) {
            UInt32 sectionLength = 0;
            if (hasHeader) {
                sectionLength = reader.ReadUInt32();
                if (sectionLength <= 0)
                    return;
            }

            var startPosition = reader.BaseStream.Position;
            var numLayers = reader.ReadInt16();

            // If numLayers < 0, then number of layers is absolute value,
            // and the first alpha channel contains the transparency data for
            // the merged result.
            if (numLayers < 0) {
                AbsoluteAlpha = true;
                numLayers = Math.Abs(numLayers);
            }
            if (numLayers == 0)
                return;

            for (int i = 0; i < numLayers; i++) {
                var layer = new Layer(reader, this);
                Layers.Add(layer);
            }

            //-----------------------------------------------------------------------

            // Load image data for all channels.
            foreach (var layer in Layers) {
                foreach (var channel in layer.Channels) {
                    channel.LoadPixelData(reader);
                }
            }

            // Length is set to 0 when called on higher bitdepth layers.
            if (sectionLength > 0) {
                // Layers Info section is documented to be even-padded, but Photoshop
                // actually pads to 4 bytes.
                var endPosition = startPosition + sectionLength;
                var positionOffset = reader.BaseStream.Position - endPosition;

                if (reader.BaseStream.Position < endPosition)
                    reader.BaseStream.Position = endPosition;
            }
        }

        ///////////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Decompress the document image data and all the layers' image data, in parallel.
        /// </summary>
        private void DecompressImages() {
            var imageLayers = Layers.Concat(new List<Layer>() { this.BaseLayer });
            foreach (var layer in imageLayers) {
                foreach (var channel in layer.Channels) {
                    var dcc = new DecompressChannelContext(channel);
                    var waitCallback = new WaitCallback(dcc.DecompressChannel);
                    ThreadPool.QueueUserWorkItem(waitCallback);
                }
            }

            foreach (var layer in Layers) {
                foreach (var channel in layer.Channels) {
                    if (channel.ID == -2)
                        layer.Masks.LayerMask.ImageData = channel.ImageData;
                    else if (channel.ID == -3)
                        layer.Masks.UserMask.ImageData = channel.ImageData;
                }
            }
        }

        /// <summary>
        /// Check the validity of the PSD file and generate necessary data.
        /// </summary>
        public void PrepareSave() {
            var imageLayers = Layers.Concat(new List<Layer>() { this.BaseLayer }).ToList();

            foreach (var layer in imageLayers) {
                layer.PrepareSave();
            }

            SetVersionInfo();
            VerifyLayerSections();
        }

        /// <summary>
        /// Verify validity of layer sections.  Each start marker should have a
        /// matching end marker.
        /// </summary>
        internal void VerifyLayerSections() {
            int depth = 0;
            foreach (var layer in Enumerable.Reverse(Layers)) {
                var layerSectionInfo = layer.AdditionalInfo.SingleOrDefault(
                  x => x is LayerSectionInfo);
                if (layerSectionInfo == null)
                    continue;

                var sectionInfo = (LayerSectionInfo)layerSectionInfo;
                switch (sectionInfo.SectionType) {
                    case LayerSectionType.OpenFolder:
                    case LayerSectionType.ClosedFolder:
                        depth++;
                        break;

                    case LayerSectionType.SectionDivider:
                        depth--;
                        if (depth < 0)
                            throw new PsdInvalidException("Layer section ended without matching start marker.");
                        break;

                    default:
                        throw new PsdInvalidException("Unrecognized layer section type.");
                }
            }

            if (depth != 0)
                throw new PsdInvalidException("Layer section not closed by end marker.");
        }

        /// <summary>
        /// Set the VersionInfo resource on the file.
        /// </summary>
        public void SetVersionInfo() {
            var versionInfo = (VersionInfo)ImageResources.Get(ResourceID.VersionInfo);
            if (versionInfo == null) {
                versionInfo = new VersionInfo();
                ImageResources.Set(versionInfo);

                // Get the version string.  We don't use the fourth part (revision).
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                var versionString = version.Major + "." + version.Minor + "." + version.Build;

                // Strings are not localized since they are not shown to the user.
                versionInfo.Version = 1;
                versionInfo.HasRealMergedData = true;
                versionInfo.ReaderName = "Paint.NET PSD Plugin";
                versionInfo.WriterName = "Paint.NET PSD Plugin " + versionString;
                versionInfo.FileVersion = 1;
            }
        }

        private void SaveLayers(PsdBinaryWriter writer) {
            using (new PsdBlockLengthWriter(writer)) {
                var numLayers = (Int16)Layers.Count;
                if (AbsoluteAlpha)
                    numLayers = (Int16)(-numLayers);

                // Layers section must be empty if the color mode doesn't allow layers.
                // Photoshop will refuse to load indexed and multichannel images if
                // there is a nonempty layers section with a layer count of 0.
                if (numLayers == 0)
                    return;

                var startPosition = writer.BaseStream.Position;
                writer.Write(numLayers);

                foreach (var layer in Layers) {
                    layer.Save(writer);
                }

                foreach (var layer in Layers) {
                    foreach (var channel in layer.Channels) {
                        channel.SavePixelData(writer);
                    }
                }

                // Documentation states that the Layers Info section is even-padded,
                // but it is actually padded to a multiple of 4.
                writer.WritePadding(startPosition, 4);
            }
        }

        ///////////////////////////////////////////////////////////////////////////

        byte[] GlobalLayerMaskData = new byte[0];

        private void LoadGlobalLayerMask(PsdBinaryReader reader) {
            var maskLength = reader.ReadUInt32();
            if (maskLength <= 0)
                return;

            GlobalLayerMaskData = reader.ReadBytes((int)maskLength);
        }

        ///////////////////////////////////////////////////////////////////////////

        private void SaveGlobalLayerMask(PsdBinaryWriter writer) {
            writer.Write((UInt32)GlobalLayerMaskData.Length);
            writer.Write(GlobalLayerMaskData);
        }

        ///////////////////////////////////////////////////////////////////////////

        #endregion

        ///////////////////////////////////////////////////////////////////////////

        #region ImageData

        ///////////////////////////////////////////////////////////////////////////

        private void LoadImage(PsdBinaryReader reader) {
            ImageCompression = (ImageCompression)reader.ReadInt16();

            // Create channels
            for (Int16 i = 0; i < ChannelCount; i++) {
                var channel = new Channel(i, this.BaseLayer);
                channel.ImageCompression = ImageCompression;
                channel.Length = this.RowCount * Util.BytesPerRow(BaseLayer.Rect, BitDepth);

                // The composite image stores all RLE headers up-front, rather than
                // with each channel.
                if (ImageCompression == ImageCompression.Rle) {
                    channel.RleRowLengths = new RleRowLengths(reader, RowCount);
                    channel.Length = channel.RleRowLengths.Total;
                }

                BaseLayer.Channels.Add(channel);
            }

            foreach (var channel in this.BaseLayer.Channels) {
                channel.ImageDataRaw = reader.ReadBytes(channel.Length);
            }

            // If there is exactly one more channel than we need, then it is the
            // alpha channel.
            if ((ColorMode != PsdColorMode.Multichannel)
              && (ChannelCount == ColorMode.MinChannelCount() + 1)) {
                var alphaChannel = BaseLayer.Channels.Last();
                alphaChannel.ID = -1;
            }
        }

        ///////////////////////////////////////////////////////////////////////////

        private void SaveImage(PsdBinaryWriter writer) {
            writer.Write((short)this.ImageCompression);
            if (this.ImageCompression == PhotoshopFile.ImageCompression.Rle) {
                foreach (var channel in this.BaseLayer.Channels)
                    channel.RleRowLengths.Write(writer);
            }
            foreach (var channel in this.BaseLayer.Channels) {
                writer.Write(channel.ImageDataRaw);
            }
        }

        ///////////////////////////////////////////////////////////////////////////

        private class DecompressChannelContext {
            private Channel ch;

            public DecompressChannelContext(Channel ch) {
                this.ch = ch;
            }

            public void DecompressChannel(object context) {
                ch.DecodeImageData();
            }
        }

        #endregion
    }


    /// <summary>
    /// The possible Compression methods.
    /// </summary>
    public enum ImageCompression {
        /// <summary>
        /// Raw data
        /// </summary>
        Raw = 0,
        /// <summary>
        /// RLE compressed
        /// </summary>
        Rle = 1,
        /// <summary>
        /// ZIP without prediction.
        /// </summary>
        Zip = 2,
        /// <summary>
        /// ZIP with prediction.
        /// </summary>
        ZipPrediction = 3
    }

}
