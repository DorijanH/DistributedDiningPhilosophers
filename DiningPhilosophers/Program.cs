using System;
using System.Collections.Generic;
using System.Threading;
using MPI;

namespace DiningPhilosophers
{
    class Program
    {
        private static int _worldSize;
        private static int _myId;

        private static bool _leftFork;
        private static bool _rightFork;

        private static bool _leftForkDirty;
        private static bool _rightForkDirty;

        //Tuple made of (sourceId, messageTag)
        private static HashSet<(int, int)> _requests = new HashSet<(int, int)>();

        private static Random _random;

        private static int _leftNeighborPhilosopherId;
        private static int _rightNeighborPhilosopherId;

        private const int LeftForkRequest = 0;
        private const int RightForkRequest = 1;
        private const int LeftForkResponse = 2;
        private const int RightForkResponse = 3;

        private static bool _sentForLeft;
        private static bool _sentForRight;

        static void Main(string[] args)
        {
            using (new MPI.Environment(ref args))
            {
                //Find out your rank and size of the world
                _worldSize = Communicator.world.Size;
                _myId = Communicator.world.Rank;
                
                //Number of processes must be greater than 1
                if (_worldSize <= 1)
                {
                    Console.Error.WriteLine($"Program must be ran with process size of n > 1. Currently n = {_worldSize}");
                    MPI.Environment.Abort(1);
                }

                //Seeding a random number generator differently for each process
                _random = new Random(Guid.NewGuid().GetHashCode() * _myId);

                //Getting the neighbors ids
                _leftNeighborPhilosopherId = ((_myId + 1) % _worldSize + _worldSize) % _worldSize;
                _rightNeighborPhilosopherId = ((_myId - 1) % _worldSize + _worldSize) % _worldSize;

                //Philosopher with smaller rank has fork
                if (_leftNeighborPhilosopherId > _myId) _leftFork = true;
                if (_rightNeighborPhilosopherId > _myId) _rightFork = true;

                //All forks are dirty at the start
                _leftForkDirty = true;
                _rightForkDirty = true;

                //Philosopher loop
                while (true)
                {
                    Think();

                    if (!_leftFork || !_rightFork)
                    {
                        AskForFork();
                    }

                    Eat();

                    AnswerQueuedRequests();
                }
            }
        }

        private static void Think()
        {
            PrintMessage("thinking");

            //Get the random number of seconds (1 - 10) the philosopher is going to think. Multiply by 2 because we will check requests every half seconds
            var randomThinkingTime = _random.Next(1, 11) * 2;

            int thinkingTime = 0;
            while (thinkingTime < randomThinkingTime)
            {
                Thread.Sleep(500);
                thinkingTime++;

                //Check for incoming requests every half a second
                CheckForRequests();
            }
        }

        private static void AskForFork()
        {
            PrintMessage($"looking for fork ({_myId})");

            do
            {
                if (!_leftFork && !_sentForLeft)
                {
                    //we are asking our left neighbor for his right fork
                    Communicator.world.ImmediateSend(true, _leftNeighborPhilosopherId, RightForkRequest);
                    _sentForLeft = true;

                }

                if (!_rightFork && !_sentForRight)
                {
                    //we are asking our right neighbor for his left fork
                    Communicator.world.ImmediateSend(true, _rightNeighborPhilosopherId, LeftForkRequest);
                    _sentForRight = true;
                }

                //We are testing if we got a message from each side - that's why we are looping 2 times. 0 - left, 1 - right
                for (int i = 0; i < 2; i++)
                {
                    int neighbor = (i == 0) ? _leftNeighborPhilosopherId : _rightNeighborPhilosopherId;

                    var incomingMessage = Communicator.world.ImmediateProbe(neighbor, Communicator.anyTag);
                    if (incomingMessage == null) continue;

                    var isForkClean = Communicator.world.Receive<bool>(incomingMessage.Source, incomingMessage.Tag);

                    //If we got the response - we got the fork
                    if (incomingMessage.Tag == LeftForkResponse || incomingMessage.Tag == RightForkResponse)
                    {
                        if (incomingMessage.Tag == LeftForkResponse)
                        {
                            _leftFork = true;
                            _leftForkDirty = !isForkClean;
                        }
                        else
                        {
                            _rightFork = true;
                            _rightForkDirty = !isForkClean;
                        }
                    }
                    //If we got the request - others want my fork
                    else
                    {
                        ProcessTheRequest(incomingMessage.Source, incomingMessage.Tag);
                    }
                }

                Thread.Sleep(400);

            } while (!_leftFork || !_rightFork);
        }

        private static void Eat()
        {
            PrintMessage("eating");

            //Random number of seconds (1 - 10) the philosopher is going to eat
            Thread.Sleep(_random.Next(1, 11) * 1000);

            _leftForkDirty = true;
            _rightForkDirty = true;
        }

        private static void AnswerQueuedRequests()
        {
            if (_requests.Count > 0)
            {
                foreach (var request in _requests)
                {
                    ProcessTheRequest(request.Item1, request.Item2);
                }
            }
        }

        private static void CheckForRequests()
        {
            var leftWaitingRequests = Communicator.world.ImmediateProbe(_leftNeighborPhilosopherId, LeftForkRequest);  
            var rightWaitingRequests = Communicator.world.ImmediateProbe(_rightNeighborPhilosopherId, RightForkRequest);

            if (leftWaitingRequests != null)
            {
                Communicator.world.Receive<bool>(leftWaitingRequests.Source, leftWaitingRequests.Tag);
                ProcessTheRequest(leftWaitingRequests.Source, leftWaitingRequests.Tag);
            }

            if (rightWaitingRequests != null)
            {
                Communicator.world.Receive<bool>(rightWaitingRequests.Source, rightWaitingRequests.Tag);
                ProcessTheRequest(rightWaitingRequests.Source, rightWaitingRequests.Tag);
            }
        }

        private static void ProcessTheRequest(int source, int tag)
        {
            if (tag == LeftForkRequest)
            {
                //If the fork is dirty, clean it up and send it to your neighbor. RightForkResponse --> we are sending him HIS right fork
                if (_leftForkDirty)
                {
                    _leftForkDirty = false;
                    _leftFork = false;
                    _sentForLeft = false;
                    Communicator.world.Send(!_leftForkDirty, source, RightForkResponse);
                }
                //If the fork is clean, save the request for after your meal
                else
                {
                    _requests.Add((source, tag));
                }
            }
            else
            {
                //If the fork is dirty, clean it up and send it to your neighbor. LeftForkResponse --> we are sending him HIS left fork
                if (_rightForkDirty)
                {
                    _rightForkDirty = false;
                    _rightFork = false;
                    _sentForRight = false;
                    Communicator.world.Send(!_rightForkDirty, source, LeftForkResponse);
                }
                //If the fork is clean, save the request for after your meal
                else
                {
                    _requests.Add((source, tag));
                }
            }
        }

        private static void PrintMessage(string message)
        {
            for (int i = 0; i < _myId; i++) Console.Write("\t");
            Console.WriteLine(message);
        }
    }
}
