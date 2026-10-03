using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Jevboard
{
    // One request carrying one Choice question per composition offset. https://docs.typesafe.ai/primitives/choice
    static class JevClient
    {
        const string Endpoint = "https://api.typesafe.ai/v1/systemone";

        public class Question
        {
            public string Id;
            public int Offset;            // composition offset this question is about
            public string KeepId = "c1";  // the option whose sentence equals the sentence as it currently stands
            public string Instructions;
            public List<KeyValuePair<string, string>> Options = new List<KeyValuePair<string, string>>();   // id -> sentence variant
        }

        // Blocking variant for worker threads. Returns null and sets error on failure.
        public static Dictionary<string, Answer> AskSync(string apiKey, string state, List<Question> questions, int timeoutMs, out string error)
        {
            Dictionary<string, Answer> result = null; string failure = null;
            ManualResetEvent done = new ManualResetEvent(false);
            Ask(apiKey, state, questions, timeoutMs, delegate(Dictionary<string, Answer> answers, string e) { result = answers; failure = e; done.Set(); });
            if (!done.WaitOne(timeoutMs + 5000)) failure = "timeout";
            error = failure;
            return failure == null ? result : null;
        }

        public class Answer
        {
            public bool Ok;
            public string Choice;
            public double Confidence = -1;
            public Dictionary<string, double> Probabilities = new Dictionary<string, double>();
        }

        // done(answers, error): error is set when the whole request failed; individual malformed answers are simply not Ok.
        public static void Ask(string apiKey, string state, List<Question> questions, int timeoutMs, Action<Dictionary<string, Answer>, string> done)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["state"] = state;
            body["model"] = "jev-latest";
            Dictionary<string, object> qs = new Dictionary<string, object>();
            Dictionary<string, HashSet<string>> ids = new Dictionary<string, HashSet<string>>();
            foreach (Question q in questions)
            {
                Dictionary<string, object> criteria = new Dictionary<string, object>();
                foreach (KeyValuePair<string, string> o in q.Options) criteria[o.Key] = o.Value;
                Dictionary<string, object> question = new Dictionary<string, object>();
                question["type"] = "choice"; question["instructions"] = q.Instructions; question["criteria"] = criteria;
                qs[q.Id] = question;
                ids[q.Id] = new HashSet<string>(criteria.Keys);
            }
            body["questions"] = qs;
            byte[] payload = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));

            ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, Answer> answers = new Dictionary<string, Answer>();
                string error = null;
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Endpoint);
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Headers["Authorization"] = "Bearer " + apiKey;
                    request.Timeout = timeoutMs; request.ReadWriteTimeout = timeoutMs;
                    using (Stream st = request.GetRequestStream()) st.Write(payload, 0, payload.Length);
                    string text;
                    using (WebResponse response = request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) text = reader.ReadToEnd();
                    Parse(text, ids, answers);
                    if (answers.Count == 0) error = "malformed response";
                }
                catch (WebException ex)
                {
                    HttpWebResponse hr = ex.Response as HttpWebResponse;
                    error = ex.Status == WebExceptionStatus.Timeout ? "timeout" : (hr != null ? "http " + (int)hr.StatusCode : ex.Status.ToString());
                }
                catch (Exception ex) { error = ex.GetType().Name; }
                done(answers, error);
            });
        }

        internal static void Parse(string text, Dictionary<string, HashSet<string>> ids, Dictionary<string, Answer> answers)
        {
            Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            Dictionary<string, object> all = root == null || !root.ContainsKey("answers") ? null : root["answers"] as Dictionary<string, object>;
            if (all == null) return;
            foreach (KeyValuePair<string, HashSet<string>> q in ids)
            {
                Answer a = new Answer();
                answers[q.Key] = a;
                Dictionary<string, object> pick = all.ContainsKey(q.Key) ? all[q.Key] as Dictionary<string, object> : null;
                if (pick == null) continue;
                string choice = pick.ContainsKey("choice") ? pick["choice"] as string : null;
                Dictionary<string, object> probs = pick.ContainsKey("probabilities") ? pick["probabilities"] as Dictionary<string, object> : null;
                if (choice == null || !q.Value.Contains(choice) || probs == null) continue;   // not in this snapshot: ignore the answer
                bool valid = true;
                foreach (KeyValuePair<string, object> kv in probs)
                {
                    double p;
                    if (!q.Value.Contains(kv.Key) || !double.TryParse(Convert.ToString(kv.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out p) || p < 0 || p > 1) { valid = false; break; }
                    a.Probabilities[kv.Key] = p;
                }
                if (!valid) { a.Probabilities.Clear(); continue; }
                if (pick.ContainsKey("confidence") && pick["confidence"] != null) a.Confidence = Convert.ToDouble(pick["confidence"], CultureInfo.InvariantCulture);
                a.Choice = choice; a.Ok = true;
            }
        }
    }
}
