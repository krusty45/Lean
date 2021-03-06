﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Indicators;
using QuantConnect.Util;

namespace QuantConnect.Algorithm.Framework.Alphas
{
    /// <summary>
    /// Uses Wilder's RSI to create insights. Using default settings, a cross over below 30 or above 70 will
    /// trigger a new insight.
    /// </summary>
    public class RsiAlphaModel : IAlphaModel
    {
        private readonly Dictionary<Symbol, SymbolData> _symbolDataBySymbol = new Dictionary<Symbol, SymbolData>();

        private readonly int _period;

        /// <summary>
        /// Initializes a new instance of the <see cref="RsiAlphaModel"/> class
        /// </summary>
        /// <param name="period">The RSI indicator period</param>
        public RsiAlphaModel(
            int period = 14
            )
        {
            _period = period;
        }

        /// <summary>
        /// Updates this alpha model with the latest data from the algorithm.
        /// This is called each time the algorithm receives data for subscribed securities
        /// </summary>
        /// <param name="algorithm">The algorithm instance</param>
        /// <param name="data">The new data available</param>
        /// <returns>The new insights generated</returns>
        public IEnumerable<Insight> Update(QCAlgorithmFramework algorithm, Slice data)
        {
            var insights = new List<Insight>();
            foreach (var kvp in _symbolDataBySymbol)
            {
                var symbol = kvp.Key;
                var rsi = kvp.Value.RSI;
                var previousState = kvp.Value.State;
                var state = GetState(rsi, previousState);

                if (state != previousState && rsi.IsReady)
                {
                    var resolution = algorithm.Securities[symbol].Resolution;
                    var insightPeriod = resolution.ToTimeSpan().Multiply(_period);

                    switch (state)
                    {
                        case State.TrippedLow:
                            insights.Add(new Insight(symbol, InsightType.Price, InsightDirection.Up, insightPeriod));
                            break;

                        case State.TrippedHigh:
                            insights.Add(new Insight(symbol, InsightType.Price, InsightDirection.Down, insightPeriod));
                            break;
                    }
                }

                kvp.Value.State = state;
            }

            return insights;
        }

        /// <summary>
        /// Cleans out old security data and initializes the RSI for any newly added securities.
        /// This functional also seeds any new indicators using a history request.
        /// </summary>
        /// <param name="algorithm">The algorithm instance that experienced the change in securities</param>
        /// <param name="changes">The security additions and removals from the algorithm</param>
        public void OnSecuritiesChanged(QCAlgorithmFramework algorithm, SecurityChanges changes)
        {
            // clean up data for removed securities
            if (changes.RemovedSecurities.Count > 0)
            {
                var removed = changes.RemovedSecurities.ToHashSet(x => x.Symbol);
                foreach (var subscription in algorithm.SubscriptionManager.Subscriptions)
                {
                    if (removed.Contains(subscription.Symbol))
                    {
                        _symbolDataBySymbol.Remove(subscription.Symbol);
                        subscription.Consolidators.Clear();
                    }
                }
            }

            // initialize data for added securities
            if (changes.AddedSecurities.Count > 0)
            {
                var newSymbolData = new List<SymbolData>();
                foreach (var added in changes.AddedSecurities)
                {
                    if (!_symbolDataBySymbol.ContainsKey(added.Symbol))
                    {
                        var rsi = algorithm.RSI(added.Symbol, _period, MovingAverageType.Wilders, added.Resolution);
                        var symbolData = new SymbolData(added.Symbol, rsi);
                        _symbolDataBySymbol[added.Symbol] = symbolData;
                        newSymbolData.Add(symbolData);
                    }
                }

                // seed new indicators using history request
                var history = algorithm.History(newSymbolData.Select(x => x.Symbol), _period);
                foreach (var slice in history)
                {
                    foreach (var symbol in slice.Keys)
                    {
                        var value = slice[symbol];
                        var list = value as IList;
                        var data = (BaseData) (list != null ? list[list.Count - 1] : value);

                        SymbolData symbolData;
                        if (_symbolDataBySymbol.TryGetValue(symbol, out symbolData))
                        {
                            symbolData.RSI.Update(data.EndTime, data.Value);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Determines the new state. This is basically cross-over detection logic that
        /// includes considerations for bouncing using the configured bounce tolerance.
        /// </summary>
        private State GetState(RelativeStrengthIndex rsi, State previous)
        {
            if (rsi > 70m)
            {
                return State.TrippedHigh;
            }

            if (rsi < 30m)
            {
                return State.TrippedLow;
            }

            if (previous == State.TrippedLow)
            {
                if (rsi > 35m)
                {
                    return State.Middle;
                }
            }

            if (previous == State.TrippedHigh)
            {
                if (rsi < 65m)
                {
                    return State.Middle;
                }
            }

            return previous;
        }

        /// <summary>
        /// Contains data specific to a symbol required by this model
        /// </summary>
        private class SymbolData
        {
            public Symbol Symbol { get; }
            public State State { get; set; }
            public RelativeStrengthIndex RSI { get; }

            public SymbolData(Symbol symbol, RelativeStrengthIndex rsi)
            {
                Symbol = symbol;
                RSI = rsi;
                State = State.Middle;
            }
        }

        /// <summary>
        /// Defines the state. This is used to prevent signal spamming and aid in bounce detection.
        /// </summary>
        private enum State
        {
            TrippedLow,
            Middle,
            TrippedHigh
        }
    }
}