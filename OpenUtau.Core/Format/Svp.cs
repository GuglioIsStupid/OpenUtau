using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.Format {
    public static class Svp {
        private const long TICK_RATE = 1470000L;
        private const string PitchCurveAbbr = "pitd";

        public static UProject Load(string filePath) {
            Log.Information("Loading SVP file: {file}", filePath);

#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
            string text = TryReadSvpText(filePath);
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
            if (string.IsNullOrWhiteSpace(text)) {
                throw new FileFormatException("Cannot read SVP file content.");
            }

            var blobs = text.Split('\0').Select(s => s.Trim('\0')).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (!blobs.Any()) {
                throw new FileFormatException("SVP file contains no JSON payload.");
            }

            var settings = new JsonSerializerSettings {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
            };

            List<ProjectDto> projects = new List<ProjectDto>();
            foreach (var blob in blobs) {
                try {
                    var proj = JsonConvert.DeserializeObject<ProjectDto>(blob, settings);
                    if (proj != null) projects.Add(proj);
                } catch (Exception ex) {
                    Log.Debug(ex, "Failed to deserialize one SVP blob; skipping.");
                }
            }
            if (!projects.Any()) {
                throw new FileFormatException("No parsable JSON project found in SVP.");
            }

            var project = projects.OrderByDescending(p => p.version ?? 0).First();

            UProject uproject = new UProject();
            Ustx.AddDefaultExpressions(uproject);
            uproject.RegisterExpression(new UExpressionDescriptor("opening", "ope", 0, 100, 100));

            uproject.FilePath = filePath;
            uproject.name = Path.GetFileNameWithoutExtension(filePath);
            try {
                var parsedTop = JObject.Parse(blobs.OrderByDescending(b => b.Length).First());
                var nameTok = parsedTop["name"] ?? parsedTop["projectName"] ?? parsedTop["title"];
                if (nameTok != null) uproject.name = nameTok.Value<string>() ?? uproject.name;
            } catch {
            }

            if (project.time?.meter != null && project.time.meter.Any()) {
                uproject.timeSignatures.Clear();
                foreach (var m in project.time.meter) {
                    uproject.timeSignatures.Add(new UTimeSignature {
                        barPosition = m.index,
                        beatPerBar = m.numerator,
                        beatUnit = m.denominator,
                    });
                }
                uproject.timeSignatures.Sort((a, b) => a.barPosition.CompareTo(b.barPosition));
                if (uproject.timeSignatures.Count > 0) uproject.timeSignatures[0].barPosition = 0;
            } else {
                Log.Warning("SVP: no time signatures found; defaulting to 4/4.");
                uproject.timeSignatures.Clear();
                uproject.timeSignatures.Add(new UTimeSignature { barPosition = 0, beatPerBar = 4, beatUnit = 4 });
            }

            if (project.time?.tempo != null && project.time.tempo.Any()) {
                uproject.tempos.Clear();
                foreach (var t in project.time.tempo) {
                    var tick = (int)(t.position / TICK_RATE);
                    var bpm = t.bpm;
                    uproject.tempos.Add(new UTempo { position = tick, bpm = bpm });
                }
                uproject.tempos.Sort((a, b) => a.position.CompareTo(b.position));
                if (uproject.tempos.Count > 0) uproject.tempos[0].position = 0;
            } else {
                Log.Warning("SVP: no tempos found; defaulting to 120 BPM.");
                uproject.tempos.Clear();
                uproject.tempos.Add(new UTempo { position = 0, bpm = 120.0 });
            }

            USinger usinger = USinger.CreateMissing("");

            var tracksDto = project.tracksSorted ?? new List<TrackDto>();
            for (int i = 0; i < tracksDto.Count; ++i) {
                var tDto = tracksDto[i];
                UTrack utrack = new UTrack(uproject) { Singer = usinger, TrackNo = i };
                utrack.TrackName = string.IsNullOrWhiteSpace(tDto.name) ? $"Track {i+1}" : tDto.name;
                utrack.Muted = tDto.renderEnabled == false;
                uproject.tracks.Add(utrack);
            }

            foreach (var tDto in tracksDto) {
                int trackIndex = tDto.dispOrder >= 0 ? tDto.dispOrder : uproject.tracks.Count;
                UTrack utrack = uproject.tracks.ElementAtOrDefault(trackIndex) ?? uproject.tracks.FirstOrDefault() ?? new UTrack(uproject) { Singer = usinger, TrackNo = 0 };

                if (tDto.mainGroup != null && tDto.mainRef != null) {
                    CreateVoicePartFromGroup(uproject, utrack, tDto.mainRef, tDto.mainGroup);
                    // For some reason this HAS to be here or else some SVPs fail to load parts for some reason ????
                }

                if (tDto.groups != null) {
                    foreach (var @ref in tDto.groups) {
                        var groupDto = project.library?.FirstOrDefault(g => g.uuid == @ref.groupID);
                        if (groupDto != null) {
                            CreateVoicePartFromGroup(uproject, utrack, @ref, groupDto);
                        } else {
                            CreateVoicePartFromGroup(uproject, utrack, @ref, new GroupDto { name = "Part" });
                        }
                    }
                }
            }

            uproject.AfterLoad();
            uproject.ValidateFull();

            Log.Information("SVP import completed: {projectName}", uproject.name);
            return uproject;
        }

        private static void CreateVoicePartFromGroup(UProject uproject, UTrack utrack, RefDto @ref, GroupDto group) {
            UVoicePart upart = new UVoicePart();
            upart.name = group.name ?? "";
            upart.comment = "";
            upart.trackNo = utrack.TrackNo;

            if (group.notes == null || !group.notes.Any()) {
                upart.position = (int)(@ref.blickOffset / TICK_RATE);
                upart.Duration = 0;
                uproject.parts.Add(upart);
                return;
            }

            var noteOnTicks = group.notes.Select(n => n.onset + @ref.blickOffset).ToList();
            var noteOffTicks = group.notes.Select(n => n.onset + n.duration + @ref.blickOffset).ToList();
            long partStartTick = noteOnTicks.Min();
            long partEndTick = noteOffTicks.Max();
            upart.position = (int)(partStartTick / TICK_RATE);
            upart.Duration = (int)((partEndTick - partStartTick) / TICK_RATE);

            foreach (var n in group.notes) {
                UNote unote = uproject.CreateNote();

                long absoluteOnset = n.onset + @ref.blickOffset;
                long absoluteDur = n.duration;
                unote.position = (int)((absoluteOnset - partStartTick) / TICK_RATE);
                unote.duration = (int)(absoluteDur / TICK_RATE);
                unote.tone = (int)(n.pitch + (@ref.pitchOffset));
                unote.lyric = string.IsNullOrWhiteSpace(n.lyrics) ? "+" : n.lyrics;

                if (unote.lyric == "-") unote.lyric = "+";

                try {
                    var phonemes = n.phonemes;
                    if (!string.IsNullOrWhiteSpace(phonemes)) {
                        var p = unote.GetType().GetProperty("phoneme");
                        if (p != null && p.CanWrite) p.SetValue(unote, phonemes);
                    }
                } catch {
                }

                int velocity = 64;
                try {
                    var attr = n.attributes;
                } catch { }

                if (uproject.expressions.TryGetValue(Ustx.VEL, out var velDesc)) {
                    unote.phonemeExpressions.Add(new UExpression(Ustx.VEL) {
                        index = 0,
                        value = velocity * 100 / 64,
                    });
                }

                if (n != null && n.attributes != null) {
                    var a = n.attributes;
                    if (a.tF0VbrStart.HasValue) unote.vibrato.@in = (float)a.tF0VbrStart.Value;
                    if (a.tF0VbrLeft.HasValue) unote.vibrato.length = (float)a.tF0VbrLeft.Value;
                    if (a.tF0VbrRight.HasValue) unote.vibrato.@out = (float)a.tF0VbrRight.Value;
                    if (a.dF0Vbr.HasValue) unote.vibrato.depth = (float)a.dF0Vbr.Value;
                    if (a.fF0Vbr.HasValue) unote.vibrato.period = (float)a.fF0Vbr.Value;
                }

                upart.notes.Add(unote);
            }

            if (group.parameters?.pitchDelta?.points != null && group.parameters.pitchDelta.points.Any()) {
                try {
                    var rawPoints = group.parameters.pitchDelta.points;
                    var pairs = new List<Tuple<long, double>>();
                    for (int i = 0; i + 1 < rawPoints.Count; i += 2) {
                        var rawTick = rawPoints[i];
                        var cent = rawPoints[i + 1];
                        long tick = (long)((rawTick + @ref.blickOffset) / TICK_RATE);
                        double cents = cent;
                        pairs.Add(Tuple.Create(tick, cents));
                    }
                    if (pairs.Any()) {
                        if (upart.curves == null) upart.curves = new List<UCurve>();
                        if (!uproject.expressions.TryGetValue(PitchCurveAbbr, out var desc)) {
                            Log.Debug("SVP: pitch curve expression {abbr} not found in project expressions", PitchCurveAbbr);
                        } else {
                            var curve = new UCurve(desc);
                            long lastT = 0;
                            int lastV = 0;
                            foreach (var p in pairs) {
                                long tProject = p.Item1;
                                int val = (int)Math.Round(p.Item2);
                                long tRelative = tProject - partStartTick;
                                if (tRelative < 0) tRelative = 0;
                                curve.Set((int)tRelative, val, (int)lastT, lastV);
                                lastT = tRelative;
                                lastV = val;
                            }
                            curve.Set(upart.Duration, lastV, (int)lastT, 0);
                            upart.curves.Add(curve);
                        }
                    }
                } catch (Exception ex) {
                    Log.Debug(ex, "Failed to map pitchDelta into UCurve; skipping.");
                }
            }

            uproject.parts.Add(upart);
        }

        private static string? TryReadSvpText(string filePath) {
            try {
                return File.ReadAllText(filePath);
            } catch (Exception ex) {
                Log.Error(ex, "Error reading SVP file");
                return null;
            }
        }

        // rubs my big belly

        private class ProjectDto {
            public List<GroupDto> library { get; set; } = new List<GroupDto>();
            public RenderConfigDto? renderConfig { get; set; }
            public TimeDto time { get; set; } = new TimeDto();
            public List<TrackDto> tracks { get; set; } = new List<TrackDto>();
            public int? version { get; set; }

            public List<TrackDto> tracksSorted {
                get {
                    return (tracks ?? new List<TrackDto>()).OrderBy(t => t.dispOrder).ToList();
                }
            }
        }

        private class RenderConfigDto {
            public string? aspirationFormat { get; set; }
            public int? bitDepth { get; set; }
            public string? destination { get; set; }
            public bool? exportMixDown { get; set; }
            public string? filename { get; set; }
            public int? numChannels { get; set; }
            public int? sampleRate { get; set; }
        }

        private class TimeDto {
            public List<MeterDto>? meter { get; set; }
            public List<TempoDto>? tempo { get; set; }
        }

        private class TrackDto {
            public string? dispColor { get; set; }
            public int dispOrder { get; set; }
            public List<RefDto>? groups { get; set; }
            public GroupDto? mainGroup { get; set; }
            public RefDto? mainRef { get; set; }
            public JObject? mixer { get; set; }
            public string? name { get; set; }
            public bool? renderEnabled { get; set; }
        }

        private class MeterDto {
            public int denominator { get; set; }
            public int index { get; set; }
            public int numerator { get; set; }
        }

        private class TempoDto {
            public double bpm { get; set; }
            public long position { get; set; }
        }

        private class GroupDto {
            public string? name { get; set; }
            public List<NoteDto> notes { get; set; } = new List<NoteDto>();
            public ParametersDto? parameters { get; set; }
            public string? uuid { get; set; }
        }

        private class RefDto {
            public JToken? audio { get; set; }
            public long blickOffset { get; set; }
            public JToken? database { get; set; }
            public string? dictionary { get; set; }
            public VoiceDto? voice { get; set; }
            public string? groupID { get; set; }
            public bool? isInstrumental { get; set; }
            public int pitchOffset { get; set; }
        }

        private class NoteDto {
            public AttributesDto? attributes { get; set; }
            public long duration { get; set; }
            public string? lyrics { get; set; }
            public long onset { get; set; }
            public string? phonemes { get; set; }
            public int pitch { get; set; }
        }

        private class ParametersDto {
            public JToken? breathiness { get; set; }
            public JToken? gender { get; set; }
            public JToken? loudness { get; set; }
            public PitchDeltaDto? pitchDelta { get; set; }
            public JToken? tension { get; set; }
            public VibratoEnvDto? vibratoEnv { get; set; }
            public JToken? voicing { get; set; }
        }

        private class AttributesDto {
            public double? tF0VbrStart { get; set; }
            public double? tF0VbrLeft { get; set; }
            public double? tF0VbrRight { get; set; }
            public double? dF0Vbr { get; set; }
            public double? pF0Vbr { get; set; }
            public double? fF0Vbr { get; set; }
        }

        private class PitchDeltaDto {
            public string? mode { get; set; }
            public List<double>? points { get; set; }
        }

        private class VibratoEnvDto {
            public string? mode { get; set; }
            public List<double>? points { get; set; }
        }

        private class VoiceDto {
            public JToken? tF0Left { get; set; }
            public JToken? tF0Right { get; set; }
            public JToken? dF0Left { get; set; }
            public JToken? dF0Right { get; set; }
            public double? tF0VbrStart { get; set; }
            public double? tF0VbrLeft { get; set; }
            public double? tF0VbrRight { get; set; }
            public double? dF0Vbr { get; set; }
            public double? fF0Vbr { get; set; }
            public JToken? paramLoudness { get; set; }
            public JToken? paramTension { get; set; }
            public JToken? paramBreathiness { get; set; }
            public JToken? paramGender { get; set; }
            public JToken? paramToneShift { get; set; }
            public JToken? renderMode { get; set; }
        }
    }
}
