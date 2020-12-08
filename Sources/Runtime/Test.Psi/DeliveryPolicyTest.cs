﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace Test.Psi
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using Microsoft.Psi;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class DeliveryPolicyTest
    {
        [TestMethod]
        [Timeout(60000)]
        public void ThrottledTimer()
        {
            int countA = 0, countB = 0, countC = 0;
            using (var p = Pipeline.Create())
            {
                Timers.Timer(p, TimeSpan.FromMilliseconds(1), (dt, ts) => countA++)
                    .Do(_ => countB++, DeliveryPolicy.Throttle)
                    .Do(
                        _ =>
                        {
                            Thread.Sleep(5);
                            countC++;
                        }, DeliveryPolicy.Throttle);

                p.RunAsync();
                p.WaitAll(100);
            }

            // Timer continues to post so messages will be dropped at receiver B until C stops throttling it
            Assert.IsTrue(countA > 0);
            Assert.IsTrue(countA > countB);
            Assert.AreEqual(countB, countC);
        }

        [TestMethod]
        [Timeout(60000)]
        public void ThrottledGenerator()
        {
            var listA = Enumerable.Range(0, 20).ToList();
            var listB = new List<int>();
            var listC = new List<int>();

            using (var p = Pipeline.Create())
            {
                Generators.Sequence(p, listA, TimeSpan.FromMilliseconds(1))
                    .Do(b => listB.Add(b), DeliveryPolicy.Throttle)
                    .Do(
                        c =>
                        {
                            Thread.Sleep(5);
                            listC.Add(c);
                        }, DeliveryPolicy.Throttle);

                p.Run();
            }

            // Generator respects throttling so all messages should make it through
            CollectionAssert.AreEqual(listA, listB);
            CollectionAssert.AreEqual(listB, listC);
        }

        // Test to make sure the DeliveryQueue returns the latest item when using a LatestMessage policy.
        [TestMethod]
        [Timeout(2000)]
        public void LatestDelivery()
        {
            int loopCount = 0;
            int lastMsg = 0;

            using (var p = Pipeline.Create())
            {
                var numGen = Generators.Range(p, 10, 3, TimeSpan.FromMilliseconds(100));
                numGen.Do(
                    m =>
                    {
                        Thread.Sleep(500);
                        loopCount++;
                        lastMsg = m;
                    }, DeliveryPolicy.LatestMessage);

                p.RunAsync();
                p.WaitAll(700);
            }

            Assert.AreEqual(2, loopCount);
            Assert.AreEqual(12, lastMsg);
        }

        [TestMethod]
        [Timeout(60000)]
        public void SynchronousDelivery()
        {
            var listA = Enumerable.Range(1, 10).ToList();
            var listB = new List<int>();
            var listC = new List<int>();

            // capture current value in thread-local storage for comparison
            ThreadLocal<int> currentValue = new ThreadLocal<int>();

            // create a pipeline consisting of three chained components A -> B -> C
            using (var p = Pipeline.Create())
            {
                Generators.Sequence(p, listA.Select(a => currentValue.Value = a), TimeSpan.FromMilliseconds(1))
                    .Do(
                        b =>
                        {
                            // in the same thread as A
                            Assert.AreEqual(currentValue.Value, b);
                            listB.Add(b);
                        }, DeliveryPolicy.SynchronousOrThrottle)
                    .Do(
                        c =>
                        {
                            // in the same thread as B (and A)
                            Assert.AreEqual(currentValue.Value, c);
                            Thread.Sleep(50); // simulate slow component
                            listC.Add(c);
                        }, DeliveryPolicy.SynchronousOrThrottle);

                p.Run();
            }

            // Since A -> B -> C delivery policy is Synchronous, all values generated by A are processed by B and C
            // synchronously in the same thread. C is slow, so it slows down B, which in turn slows down A.
            CollectionAssert.AreEqual(listA, listB);
            CollectionAssert.AreEqual(listB, listC);
        }

        [TestMethod]
        [Timeout(60000)]
        public void SynchronousWithUnlimited()
        {
            var listA = Enumerable.Range(1, 10).ToList();
            var listB = new List<int>();
            var listC = new List<int>();

            // capture current value in thread-local storage for comparison
            ThreadLocal<int> currentValue = new ThreadLocal<int>();

            // create a pipeline consisting of three chained components A -> B -> C
            using (var p = Pipeline.Create())
            {
                Generators.Sequence(p, listA.Select(a => currentValue.Value = a), TimeSpan.FromMilliseconds(1))
                    .Do(
                        b =>
                        {
                            // not in the same thread as A (A -> B delivery policy is Unlimited)
                            Assert.AreNotEqual(currentValue.Value, b);
                            currentValue.Value = b;
                            listB.Add(b);
                        }, DeliveryPolicy.Unlimited)
                    .Do(
                        c =>
                        {
                            // in the same thread as B (B -> C delivery policy is Synchronous)
                            Assert.AreEqual(currentValue.Value, c);
                            Thread.Sleep(50); // simulate slow component
                            listC.Add(c);
                        }, DeliveryPolicy.SynchronousOrThrottle);

                p.Run();
            }

            // Since A -> B delivery policy is Unlimited, no messages are dropped at receiver B. B's receiver
            // queue will grow due to the fact that it is executing synchronously with C (which is slow), but
            // asynchronously with A (which is fast).
            CollectionAssert.AreEqual(listA, listB);

            // Since B -> C delivery policy is Synchronous, all messages from B are processed by C.
            CollectionAssert.AreEqual(listB, listC);
        }

        [TestMethod]
        [Timeout(60000)]
        public void SynchronousWithLatestMessage()
        {
            var listA = Enumerable.Range(1, 10).ToList();
            var listB = new List<int>();
            var listC = new List<int>();

            // capture current value in thread-local storage for comparison
            ThreadLocal<int> currentValue = new ThreadLocal<int>();

            // create a pipeline consisting of three chained components A -> B -> C
            using (var p = Pipeline.Create())
            {
                Generators.Sequence(p, listA.Select(a => currentValue.Value = a), TimeSpan.FromMilliseconds(1))
                    .Do(
                        b =>
                        {
                            // not in the same thread as A (A -> B delivery policy is LatestMessage)
                            Assert.AreNotEqual(currentValue.Value, b);
                            currentValue.Value = b;
                            listB.Add(b);
                        }, DeliveryPolicy.LatestMessage)
                    .Do(
                        c =>
                        {
                            // in the same thread as B (B -> C delivery policy is Synchronous)
                            Assert.AreEqual(currentValue.Value, c);
                            Thread.Sleep(50); // simulate slow component
                            listC.Add(c);
                        }, DeliveryPolicy.SynchronousOrThrottle);

                p.Run();
            }

            // Since A -> B delivery policy is LatestMessage, messages are dropped at receiver B. Even though B is not inherently
            // slow, it is effectively throttled due to the fact that it is executing synchronously with C, which is slow. Generator
            // A continues to generate messages fast, which then get dropped by B until it is able to process the next message.
            CollectionAssert.AreNotEqual(listA, listB);

            // Since B -> C delivery policy is Synchronous, all messages from B are processed by C.
            CollectionAssert.AreEqual(listB, listC);
        }

        [TestMethod]
        [Timeout(60000)]
        public void DeliveryPolicyWithGuarantees()
        {
            var latest = new List<int>();
            var latestWithGuarantees = new List<int>();
            var latestWithGuaranteesChained = new List<int>();
            var queueSizeTwoWithGuarantees = new List<int>();

            // create a pipeline consisting of three chained components A -> B -> C
            using (var p = Pipeline.Create())
            {
                var generator = Generators.Range(p, 0, 10, TimeSpan.FromMilliseconds(1));
                generator
                    .Do(_ => { Thread.Sleep(50); }, DeliveryPolicy.LatestMessage)
                    .Do(m => latest.Add(m));

                generator
                    .Do(_ => { Thread.Sleep(50); }, DeliveryPolicy.LatestMessage.WithGuarantees<int>(i => i % 2 == 0))
                    .Do(m => latestWithGuarantees.Add(m));

                generator
                    .Do(_ => { Thread.Sleep(50); }, DeliveryPolicy.LatestMessage.WithGuarantees<int>(i => i % 2 == 0).WithGuarantees(i => i % 3 == 0))
                    .Do(m => latestWithGuaranteesChained.Add(m));

                generator
                    .Do(_ => { Thread.Sleep(50); }, DeliveryPolicy.QueueSizeConstrained(2).WithGuarantees<int>(i => i % 2 == 0))
                    .Do(m => queueSizeTwoWithGuarantees.Add(m));

                p.Run();
            }

            // with latest we may drop some messages except the last message.
            CollectionAssert.IsSubsetOf(new List<int>() { 9 }, latest);
            CollectionAssert.AllItemsAreUnique(latest); // ensure uniqueness
            CollectionAssert.AreEqual(latest.OrderBy(x => x).ToList(), latest); // ensure order

            // with guarantees we get all the even messages.
            CollectionAssert.IsSubsetOf(new List<int>() { 0, 2, 4, 6, 8 }, latestWithGuarantees);
            CollectionAssert.AllItemsAreUnique(latestWithGuarantees);
            CollectionAssert.AreEqual(latestWithGuarantees.OrderBy(x => x).ToList(), latestWithGuarantees);

            // with guarantees we get all the even and multiple of 3 messages.
            CollectionAssert.IsSubsetOf(new List<int>() { 0, 2, 3, 4, 6, 8, 9 }, latestWithGuaranteesChained);
            CollectionAssert.AllItemsAreUnique(latestWithGuaranteesChained);
            CollectionAssert.AreEqual(latestWithGuaranteesChained.OrderBy(x => x).ToList(), latestWithGuaranteesChained);

            // with guarantees, even when queue size is 2, we get all the even messages.
            CollectionAssert.IsSubsetOf(new List<int>() { 0, 2, 4, 6, 8 }, queueSizeTwoWithGuarantees);
            CollectionAssert.AllItemsAreUnique(queueSizeTwoWithGuarantees);
            CollectionAssert.AreEqual(queueSizeTwoWithGuarantees.OrderBy(x => x).ToList(), queueSizeTwoWithGuarantees);
        }
    }
}