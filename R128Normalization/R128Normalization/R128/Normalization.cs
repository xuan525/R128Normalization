﻿using R128.Limiter;
using R128.Lufs;
using R128.Utils;
using System;
using System.IO;
using System.Text;
using Wav;

/*
 *    This file is a part of the R128Normalization utils
 *    Copyright (C) 2020  Xuan525
 *
 *    This program is free software: you can redistribute it and/or modify
 *    it under the terms of the GNU General Public License as published by
 *    the Free Software Foundation, either version 3 of the License, or
 *    (at your option) any later version.
 *
 *    This program is distributed in the hope that it will be useful,
 *    but WITHOUT ANY WARRANTY; without even the implied warranty of
 *    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *    GNU General Public License for more details.
 *
 *    You should have received a copy of the GNU General Public License
 *    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 *    
 *    Email : shanboxuan@me.com
 *    Github : https://github.com/xuan525
*/

namespace R128
{
    /// <summary>
    /// EBU R128 standard : https://tech.ebu.ch/docs/r/r128-2014.pdf
    /// </summary>
    public class Normalization
    {
        /// <summary>
        /// Normalize a file using EBU R128 standard
        /// </summary>
        /// <param name="inputFile">Input filename</param>
        /// <param name="outputFile">Output filename</param>
        public static void Normalize(string inputFile, string outputFile)
        {
            try
            {
                // Decode
                WavReader wavReader;
                try
                {
                    using (FileStream fileStream = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        wavReader = new WavReader(fileStream, Encoding.Default);
                    }
                }
                catch (WavReader.FormatNotSupportedException ex)
                {
                    Stream ffStream;
                    try
                    {
                        ffStream = FFmpegWavPiper.GetS32WavStream(inputFile, FFmpegLogReceived);
                    }
                    catch (FFmpegWavPiper.FFmepgNotFoundException)
                    {
                        Console.WriteLine("FFmpeg not found. Non-wav files require ffmpeg for decoding support. File skipped.");
                        return;
                    }
                    wavReader = new WavReader(ffStream, Encoding.UTF8);
                }

                WavReader.FmtChunk fmt = (WavReader.FmtChunk)wavReader.Riff.Chunks["fmt "];
                double[][] buffer = wavReader.GetSampleData();

                // Normalize
                buffer = Normalize(buffer, fmt.SampleRate);

                // Encode
                WavWriter wavWriter = new WavWriter(buffer, fmt.SampleRate, 0x0003, 32, null, Encoding.Default);
                if (wavReader.Riff.Chunks.ContainsKey("LIST"))
                {
                    wavWriter.Infos = ((WavReader.ListChunk)wavReader.Riff.Chunks["LIST"]).Data;
                }
                else
                {
                    wavWriter.Infos = new System.Collections.Generic.SortedDictionary<string, string>();
                }
                wavWriter.Infos["ISFT"] = "Build-in codec";
                wavWriter.Infos["ITCH"] = "Demo中文测试";
                wavWriter.Save(outputFile);
                Console.WriteLine("File saved: {0}", Path.GetFileNameWithoutExtension(outputFile));
            }
            catch (Exception ex)
            {
                Console.WriteLine("An exception has occurred : ");
                Console.WriteLine($"{ex.GetType()} : {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            
        }

        /// <summary>
        /// Normalize a buffer
        /// </summary>
        /// <param name="buffer">The buffer need to be normalize</param>
        /// <param name="sampleRate">The sample rate of the sample data</param>
        /// <returns>Normalized buffer</returns>
        public static double[][] Normalize(double[][] buffer, double sampleRate)
        {
            // Calc input loudness
            R128LufsMeter r128LufsMeter = new R128LufsMeter();
            r128LufsMeter.Prepare(sampleRate, buffer.Length);
            Console.Write("Calculating input loudness...");
            r128LufsMeter.StartIntegrated();
            R128LufsMeter.Result[] results = r128LufsMeter.ProcessBuffer(buffer,
                (double current, double total) => { AppendConsoleProgressBar($"Calculating input loudness : {current:N0}/{total:N0}", (double)current / total); });
            r128LufsMeter.StopIntegrated();

            // Report input loudness
            double integratedLoudness = r128LufsMeter.IntegratedLoudness;
            ConsoleClearLine();
            Console.WriteLine("Input integrated loudness : {0:N} LU", integratedLoudness);

            // Normalization to -23 LU
            double targetLoudness = -23;
            double gain = targetLoudness - integratedLoudness;
            int count = 0;
            double[][] clone = null;
            while (Math.Abs(targetLoudness - integratedLoudness) > 0.5)
            {
                count++;

                // Clone buffer
                clone = new double[buffer.Length][];
                for (int i = 0; i < buffer.Length; i++)
                {
                    clone[i] = (double[])buffer[i].Clone();
                }

                // Apply gain to normalize to -23dB
                Console.Write("Applying gain...");
                Gain.ApplyGain(clone, gain);
                ConsoleClearLine();
                Console.WriteLine($"Gain applyed : {gain:N} dB");

                // Limit to -1 dB True Peak
                TruePeakLimiter.ProcessBuffer(clone, -1, sampleRate, 0.001, 0.8,
                    (double current, double total) => { if (current % 10000 == 0) { AppendConsoleProgressBar($"Limiting : {current:N0}/{total:N0}", (double)current / total); } },
                    //(double env) => streamWriter.WriteLine(env)
                    null);
                ConsoleClearLine();
                Console.WriteLine("Limiting finished!");

                // Calc output loudness
                Console.Write("Verifying output loudness...");
                r128LufsMeter.StartIntegrated();
                results = r128LufsMeter.ProcessBuffer(clone,
                    (double current, double total) => { AppendConsoleProgressBar($"Verifying output loudness : {current:N0}/{total:N0}", (double)current / total); });
                r128LufsMeter.StopIntegrated();

                // Report output loudness
                integratedLoudness = r128LufsMeter.IntegratedLoudness;
                ConsoleClearLine();
                Console.WriteLine("Output integrated loudness {0} : {1:N} LU", count, integratedLoudness);
                gain += targetLoudness - integratedLoudness;
            }

            if(clone != null)
            {
                return clone;
            }
            return buffer;
        }

        private static void ConsoleClearLine()
        {
            string clearString = '\r' + new string(' ', Console.WindowWidth - 1) + '\r';
            Console.Write(clearString);
        }

        private static void AppendConsoleProgressBar(string prefix, double progress)
        {
            int progressBarLength = Console.WindowWidth - 1 - prefix.Length;
            string postfix = progress.ToString("P");
            int innerLength = progressBarLength - 4 - postfix.Length;

            StringBuilder stringBuilder = new StringBuilder(prefix.Length + progressBarLength);
            stringBuilder.Append('\r');
            stringBuilder.Append(prefix);
            stringBuilder.Append(" [");
            for (int i = 0; i < innerLength; i++)
            {
                if(((double)i / innerLength) < progress)
                {
                    stringBuilder.Append('*');
                }
                else
                {
                    stringBuilder.Append(' ');
                }
            }
            stringBuilder.Append("] ");
            stringBuilder.Append(postfix);
            Console.Write(stringBuilder.ToString());
        }

        private static void GenarateLoudnessReport(R128LufsMeter.Result[] results, string filename)
        {
            using (StreamWriter streamWriter = new StreamWriter(filename))
            {
                streamWriter.WriteLine("Index, Momentary, ShortTerm");
                for (int i = 0; i < results.Length; i++)
                {
                    R128LufsMeter.Result result = results[i];
                    streamWriter.WriteLine("{0}, {1}, {2}", i,
                        result.MomentaryLoudness > -100 ? result.MomentaryLoudness : -100,
                        result.ShortTermLoudness > -100 ? result.ShortTermLoudness : -100);
                }
            }
        }

        private static void FFmpegLogReceived(object sender, System.Diagnostics.DataReceivedEventArgs e)
        {
            //Console.WriteLine(e.Data);
        }
    }
}
