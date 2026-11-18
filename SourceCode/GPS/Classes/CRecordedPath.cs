using AgOpenGPS.Core.Models;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public class CRecPathPt
    {
        public double easting { get; set; }
        public double northing { get; set; }
        public double heading { get; set; }
        public double speed { get; set; }
        public bool autoBtnState { get; set; }

        //constructor
        public CRecPathPt(double _easting, double _northing, double _heading, double _speed,
                            bool _autoBtnState)
        {
            easting = _easting;
            northing = _northing;
            heading = _heading;
            speed = _speed;
            autoBtnState = _autoBtnState;
        }

        public GeoCoord AsGeoCoord => new GeoCoord(northing, easting);
    }

    public class CRecordedPath
    {
        //constructor
        public CRecordedPath(FormGPS _f)
        {
            mf = _f;
        }

        //pointers to mainform controls
        private readonly FormGPS mf;

        //the recorded path from driving around
        public List<CRecPathPt> recList = new List<CRecPathPt>();

        //the dubins path to get there
        public List<CRecPathPt> shuttleDubinsList = new List<CRecPathPt>();

        public int shuttleListCount;

        //list of vec3 points of Dubins shortest path between 2 points - To be converted to RecPt
        public List<vec3> shortestDubinsList = new List<vec3>();

        //generated reference line
        public double distanceFromCurrentLinePivot;
        private int A, B, C;

        public int currentPositonIndex;

        //pure pursuit values
        public vec3 pivotAxlePosRP = new vec3(0, 0, 0);

        public vec3 homePos = new vec3();
        public vec2 goalPointRP = new vec2(0, 0);
        public double steerAngleRP, rEastRP, rNorthRP, ppRadiusRP;
        public vec2 radiusPointRP = new vec2(0, 0);

        public bool isEndOfTheRecLine, isRecordOn;
        public bool isDrivingRecordedPath, isFollowingDubinsToPath, isFollowingRecPath, isFollowingDubinsHome;

        public double pivotDistanceError, pivotDistanceErrorLast, pivotDerivative;

        //derivative counters
        private int counter2;

        public double inty;
        public double pivotErrorTotal;

        public int resumeState;

        private int starPathIndx = 0;

        // Simulator speed while driving a path: the path speed scaled by the user's choice.
        // When the sim speed buttons are used, the new ratio to the path speed is kept, so turns
        // stay slower than passes but at the speed level the user picked.
        private double simSpeedFactor = 1.0;
        private bool lastPathReverse;
        private int cuspHoldTicks;
        private double lastSimStep = double.NaN;

        private void SetSimSpeed(double pathStep)
        {
            // a reverse part of the path has a negative step: the speed factor is the ratio of
            // the magnitudes (a signed ratio went to zero and the simulated tractor stopped before
            // the reversing), the sign of the path step is kept
            if (!double.IsNaN(lastSimStep) && Math.Abs(mf.sim.stepDistance - lastSimStep) > 1e-6 && Math.Abs(pathStep) > 1e-6)
                simSpeedFactor = Math.Abs(mf.sim.stepDistance) / Math.Abs(pathStep);
            lastSimStep = pathStep * simSpeedFactor;
            mf.sim.stepDistance = lastSimStep;
        }

        public bool StartDrivingRecordedPath()
        {
            lastSimStep = double.NaN;
            lastPathReverse = false;
            cuspHoldTicks = 0;
            //create the dubins path based on start and goal to start of recorded path
            A = B = C = 0;

            if (recList.Count < 5) return false;

            //save a copy of where we started.
            homePos = mf.pivotAxlePos;

            // Try to find the nearest point of the recordet path in relation to the current position:
            double distance = double.MaxValue;
            int idx = 0;
            int i = 0;

            if (resumeState == 0) //start at the start
            {
                currentPositonIndex = 0;
                idx = 0;
                starPathIndx = 0;
            }
            else if (resumeState == 1) //resume from where stopped mid path
            {
                if (currentPositonIndex + 5 > recList.Count)
                {
                    currentPositonIndex = 0;
                    idx = 0;
                    starPathIndx = 0;
                }
                else
                {
                    idx = starPathIndx = currentPositonIndex;
                }
            }
            else //find closest point
            {
                // nearest point driven in the direction the vehicle faces (the pass beside it,
                // driven the other way, can be just as near); any point when none faces this way
                double vehHeading = mf.pivotAxlePos.heading;
                double bestAny = double.MaxValue;
                int idxAny = 0;
                foreach (CRecPathPt pt in recList)
                {
                    double temp = ((pt.easting - homePos.easting) * (pt.easting - homePos.easting))
                        + ((pt.northing - homePos.northing) * (pt.northing - homePos.northing));

                    if (temp < bestAny)
                    {
                        bestAny = temp;
                        idxAny = i;
                    }
                    if (temp < distance && Math.Cos(pt.heading - vehHeading) > 0.5)
                    {
                        distance = temp;
                        idx = i;
                    }
                    i++;
                }
                if (distance > bestAny + 400.0) { idx = idxAny; distance = bestAny; }

                // on the path already (within 3 m, facing along it): follow it from here. An
                // approach curve to a point a few metres ahead has no room and made a full circle
                if (distance < 9.0 && Math.Cos(recList[idx].heading - vehHeading) > 0.7 && idx + 2 < recList.Count)
                {
                    starPathIndx = currentPositonIndex = idx;
                    C = idx;
                    A = idx;
                    B = idx + 1;
                    shuttleDubinsList.Clear();
                    shortestDubinsList.Clear();
                    isFollowingDubinsHome = false;
                    isFollowingDubinsToPath = false;
                    isFollowingRecPath = true;
                    isEndOfTheRecLine = false;
                    isDrivingRecordedPath = true;
                    return true;
                }

                // farther away: join the path about 12 m on, so the approach curve has room;
                // not past a change of driving direction
                double on = 0;
                while (idx + 1 < recList.Count - 1 && on < 12.0 && (recList[idx + 1].speed < 0) == (recList[idx].speed < 0))
                {
                    on += glm.Distance(recList[idx].easting, recList[idx].northing, recList[idx + 1].easting, recList[idx + 1].northing);
                    idx++;
                }

                starPathIndx = currentPositonIndex = idx;
            }

            //the goal is the first point of path, the start is the current position
            vec3 goal = new vec3(recList[idx].easting, recList[idx].northing, recList[idx].heading);

            //get the dubins for approach to recorded path
            GetDubinsPath(goal);
            shuttleListCount = shuttleDubinsList.Count;

            //has a valid dubins path been created?
            if (shuttleListCount == 0) return false;

            //starPathIndx = idxFieldSelected;

            //technically all good if we get here so set all the flags
            isFollowingDubinsHome = false;
            isFollowingRecPath = false;
            isFollowingDubinsToPath = true;
            isEndOfTheRecLine = false;
            //currentPositonIndex = 0;
            isDrivingRecordedPath = true;
            return true;
        }

        public bool trig;
        public double north;
        public int pathCount = 0;

        public void UpdatePosition()
        {
            if (isFollowingDubinsToPath)
            {
                //set a speed of 10 kmh
                SetSimSpeed(shuttleDubinsList[C].speed / 50);

                pivotAxlePosRP = mf.pivotAxlePos;

                //StanleyDubinsPath(shuttleListCount);
                PurePursuitDubins(shuttleListCount);

                //check if close to recorded path
                int cnt = shuttleDubinsList.Count;
                pathCount = cnt - B;
                if (pathCount < 8)
                {
                    CRecPathPt startPt = recList[starPathIndx];
                    double distSqr = glm.DistanceSquared(pivotAxlePosRP.northing, pivotAxlePosRP.easting, startPt.northing, startPt.easting);

                    // already past the start point (in its direction of travel)?
                    double ahead = (pivotAxlePosRP.easting - startPt.easting) * Math.Sin(startPt.heading)
                                 + (pivotAxlePosRP.northing - startPt.northing) * Math.Cos(startPt.heading);

                    // Switch to the path when close, when the start was passed, or at the end of the
                    // approach. Only 1.4 m used to be accepted, so a small miss kept the vehicle in
                    // the approach for ever.
                    if (distSqr < 16 || (ahead > 0 && distSqr < 100) || pathCount <= 2)
                    {
                        isFollowingRecPath = true;
                        isFollowingDubinsToPath = false;
                        shuttleDubinsList.Clear();
                        shortestDubinsList.Clear();

                        // continue from the nearest path point just after the start
                        int best = starPathIndx;
                        double bestD = double.MaxValue;
                        int top = Math.Min(recList.Count - 2, starPathIndx + 80);
                        for (int i = starPathIndx; i <= top; i++)
                        {
                            double d = glm.DistanceSquared(pivotAxlePosRP.northing, pivotAxlePosRP.easting, recList[i].northing, recList[i].easting);
                            if (d < bestD) { bestD = d; best = i; }
                        }
                        currentPositonIndex = best;
                        C = best;
                        A = C + 1 < recList.Count ? C + 1 : C;
                        B = A + 1 < recList.Count ? A + 1 : A;
                    }
                }
            }

            if (isFollowingRecPath)
            {
                pivotAxlePosRP = mf.pivotAxlePos;

                //StanleyRecPath(recListCount);
                PurePursuitRecPath(recList.Count);

                //if end of the line then stop
                if (!isEndOfTheRecLine)
                {
                    // at a change of driving direction (cusp of a three point turn) the simulated vehicle
                    // stops until the wheels have turned to the other side, as a driver does; moving off
                    // with the wheels still turned the wrong way ran the second arc up to 2 m off the path
                    double stepSpeed = recList[C].speed / 34.86;
                    bool nowReverse = recList[C].speed < 0;
                    if (nowReverse != lastPathReverse) { lastPathReverse = nowReverse; cuspHoldTicks = 60; }
                    if (cuspHoldTicks > 0)
                    {
                        cuspHoldTicks--;
                        if (Math.Abs(mf.mc.actualSteerAngleDegrees - steerAngleRP) > 2.0) stepSpeed = 0;
                    }
                    SetSimSpeed(stepSpeed);
                    north = recList[C].northing;

                    pathCount = recList.Count - C;

                    //section control - only if different click the button
                    //Painting (and the section valves) lag behind the switching by the look ahead
                    //times, so switch that far ahead of where the work starts / ends on the path,
                    //the same way the look ahead does it in normal section control
                    bool autoBtn = (mf.autoBtnState == btnStates.Auto);
                    trig = autoBtn;
                    double mps = Math.Abs(mf.avgSpeed) / 3.6;
                    double leadOn = mps * mf.tool.lookAheadOnSetting;
                    double leadOff = mps * (mf.tool.lookAheadOffSetting > 0
                        ? mf.tool.lookAheadOffSetting + 4.0 / mf.gpsHz
                        : mf.tool.turnOffDelay);
                    bool wanted = autoBtn ? WorkFlagAhead(C, leadOff) : WorkFlagAhead(C, leadOn);
                    if (autoBtn != wanted) mf.btnSectionMasterAuto.PerformClick();
                }
                else
                {
                    StopDrivingRecordedPath();
                    // the plan is done: guidance goes on with the nearer headland track
                    mf.SwitchToNearestHeadlandTrack();
                    return;

                    //create the dubins path based on start and goal to start trip home
                    //GetDubinsPath(homePos);
                    //shuttleListCount = shuttleDubinsList.Count;

                    ////its too small
                    //if (shuttleListCount < 3)
                    //{
                    //    StopDrivingRecordedPath();
                    //    return;
                    //}

                    ////set all the flags
                    //isFollowingDubinsHome = true;
                    //A = B = C = 0;
                    //isFollowingRecPath = false;
                    //isFollowingDubinsToPath = false;
                    //isEndOfTheRecLine = false;
                }
            }

            if (isFollowingDubinsHome)
            {
                int cnt = shuttleDubinsList.Count;
                pathCount = cnt - B;
                if (pathCount < 3)
                {
                    StopDrivingRecordedPath();
                    return;
                }

                SetSimSpeed(shuttleDubinsList[C].speed / 35);
                pivotAxlePosRP = mf.pivotAxlePos;

                //StanleyDubinsPath(shuttleListCount);
                PurePursuitDubins(shuttleListCount);
            }
        }

        public void StopDrivingRecordedPath()
        {
            isFollowingDubinsHome = false;
            isFollowingRecPath = false;
            isFollowingDubinsToPath = false;
            shuttleDubinsList.Clear();
            shortestDubinsList.Clear();
            mf.sim.stepDistance = 0;
            isDrivingRecordedPath = false;
            mf.btnPathGoStop.Image = Properties.Resources.boundaryPlay;
            mf.btnPathRecordStop.Enabled = true;
            mf.btnPickPath.Enabled = true;
            mf.btnResumePath.Enabled = true;
        }

        private void GetDubinsPath(vec3 goal)
        {
            CDubins.turningRadius = mf.yt.youTurnRadius * 1.2;
            CDubins dubPath = new CDubins();

            // current psition
            pivotAxlePosRP = mf.pivotAxlePos;

            //bump it forward
            vec3 pt2 = new vec3
            {
                easting = pivotAxlePosRP.easting + (Math.Sin(pivotAxlePosRP.heading) * 3),
                northing = pivotAxlePosRP.northing + (Math.Cos(pivotAxlePosRP.heading) * 3),
                heading = pivotAxlePosRP.heading
            };

            //get the dubins path vec3 point coordinates of turn
            shortestDubinsList.Clear();
            shuttleDubinsList.Clear();

            shortestDubinsList = dubPath.GenerateDubins(pt2, goal);

            //if Dubins returns 0 elements, there is an unavoidable blockage in the way.
            if (shortestDubinsList.Count > 0)
            {
                shortestDubinsList.Insert(0, mf.pivotAxlePos);

                //transfer point list to recPath class point style
                for (int i = 0; i < shortestDubinsList.Count; i++)
                {
                    CRecPathPt pt = new CRecPathPt(shortestDubinsList[i].easting, shortestDubinsList[i].northing, shortestDubinsList[i].heading, 9.0, false);
                    shuttleDubinsList.Add(pt);
                }
                return;
            }
        }

        // Work flag of the path point the given distance ahead of point 'from'
        private bool WorkFlagAhead(int from, double dist)
        {
            int k = from;
            double acc = 0;
            while (k + 1 < recList.Count && acc < dist)
            {
                double dx = recList[k + 1].easting - recList[k].easting;
                double dz = recList[k + 1].northing - recList[k].northing;
                acc += Math.Sqrt(dx * dx + dz * dz);
                k++;
            }
            return recList[k].autoBtnState;
        }

        private void PurePursuitRecPath(int ptCount)
        {
            //find the closest 2 points to current fix
            double minDistA = 9999999999;
            double dist, dx, dz;

            // the path can have been replaced (another one loaded) since the last position
            if (ptCount < 2) return;
            if (currentPositonIndex < 0 || currentPositonIndex > ptCount - 1) currentPositonIndex = 0;
            A = Math.Max(0, Math.Min(ptCount - 1, A));

            //set the search range close to current position
            int top = currentPositonIndex + 5;
            if (top > ptCount) top = ptCount;

            // a change of driving direction (cusp of a three point turn) just ahead: the path
            // comes back next to itself there, so the closest point must not jump onto the next
            // part before the cusp is reached; once it is reached the search starts after it
            for (int t = currentPositonIndex; t < top - 1; t++)
            {
                if ((recList[t].speed < 0) == (recList[t + 1].speed < 0)) continue;
                int pr = Math.Max(0, t - 1);
                double tx = recList[t].easting - recList[pr].easting, tz = recList[t].northing - recList[pr].northing;
                double ax = pivotAxlePosRP.easting - recList[t].easting, az = pivotAxlePosRP.northing - recList[t].northing;
                if (pr == t || (ax * tx) + (az * tz) >= 0 || (ax * ax) + (az * az) < 0.09)
                {
                    currentPositonIndex = t + 1;
                    top = Math.Min(ptCount, currentPositonIndex + 5);
                }
                else top = t + 1;
                break;
            }

            for (int t = currentPositonIndex; t < top; t++)
            {
                dist = ((pivotAxlePosRP.easting - recList[t].easting) * (pivotAxlePosRP.easting - recList[t].easting))
                                + ((pivotAxlePosRP.northing - recList[t].northing) * (pivotAxlePosRP.northing - recList[t].northing));
                if (dist < minDistA)
                {
                    minDistA = dist;
                    A = t;
                }
            }

            //Save the closest point
            C = A;

            //next point is the next in list
            B = A + 1;
            if (B == ptCount)
            {
                //don't go past the end of the list - "end of the line" trigger
                A--;
                B--;
                isEndOfTheRecLine = true;
            }

            //save current position
            currentPositonIndex = A;

            //get the distance from currently active AB line
            dx = recList[B].easting - recList[A].easting;
            dz = recList[B].northing - recList[A].northing;

            if (Math.Abs(dx) < Double.Epsilon && Math.Abs(dz) < Double.Epsilon) return;

            //how far from current AB Line is fix
            distanceFromCurrentLinePivot = ((dz * pivotAxlePosRP.easting) - (dx * pivotAxlePosRP.northing) + (recList[B].easting
                        * recList[A].northing) - (recList[B].northing * recList[A].easting))
                            / Math.Sqrt((dz * dz) + (dx * dx));

            //integral slider is set to 0
            if (mf.vehicle.purePursuitIntegralGain != 0)
            {
                pivotDistanceError = distanceFromCurrentLinePivot * 0.2 + pivotDistanceError * 0.8;

                if (counter2++ > 4)
                {
                    pivotDerivative = pivotDistanceError - pivotDistanceErrorLast;
                    pivotDistanceErrorLast = pivotDistanceError;
                    counter2 = 0;
                    pivotDerivative *= 2;

                    //limit the derivative
                    //if (pivotDerivative > 0.03) pivotDerivative = 0.03;
                    //if (pivotDerivative < -0.03) pivotDerivative = -0.03;
                    //if (Math.Abs(pivotDerivative) < 0.01) pivotDerivative = 0;
                }

                //pivotErrorTotal = pivotDistanceError + pivotDerivative;

                if (isFollowingRecPath
                    && Math.Abs(pivotDerivative) < (0.1)
                    && mf.avgSpeed > 2.5)
                //&& Math.Abs(pivotDistanceError) < 0.2)

                {
                    //if over the line heading wrong way, rapidly decrease integral
                    if ((inty < 0 && distanceFromCurrentLinePivot < 0) || (inty > 0 && distanceFromCurrentLinePivot > 0))
                    {
                        inty += pivotDistanceError * mf.vehicle.purePursuitIntegralGain * -0.04;
                    }
                    else
                    {
                        if (Math.Abs(distanceFromCurrentLinePivot) > 0.02)
                        {
                            inty += pivotDistanceError * mf.vehicle.purePursuitIntegralGain * -0.02;
                            if (inty > 0.2) inty = 0.2;
                            else if (inty < -0.2) inty = -0.2;
                        }
                    }
                }
                else inty *= 0.95;
            }
            else inty = 0;

            if (mf.isReverse) inty = 0;

            // ** Pure pursuit ** - calc point on ABLine closest to current position
            double U = (((pivotAxlePosRP.easting - recList[A].easting) * dx)
                        + ((pivotAxlePosRP.northing - recList[A].northing) * dz))
                        / ((dx * dx) + (dz * dz));

            rEastRP = recList[A].easting + (U * dx);
            rNorthRP = recList[A].northing + (U * dz);

            //update base on autosteer settings and distance from line
            double goalPointDistance = mf.vehicle.UpdateGoalPointDistance();

            // a planned reverse part (negative speed) is still followed along the path order
            bool ReverseHeading = !mf.isReverse || recList[A].speed < 0;

            int count = ReverseHeading ? 1 : -1;
            CRecPathPt start = new CRecPathPt(rEastRP, rNorthRP, 0, 0, false);
            double distSoFar = 0;
            bool goalFound = false;
            int lastIdx = -1;

            bool partReverse = recList[A].speed < 0;
            for (int i = ReverseHeading ? B : A; i < ptCount && i >= 0; i += count)
            {
                // the driving direction changes here (cusp of a three point turn): the goal point
                // must not run on to the next part, which comes back beside this one and turned
                // the wheels the other way before the cusp. It goes on past the cusp along the
                // curve being driven, so the steering is held up to the stop
                if (count == 1 && i > 1 && (recList[i].speed < 0) != partReverse)
                {
                    // curve being driven: from points about a metre and two metres back (points
                    // right at the cusp can be close together or uneven)
                    int c2 = i - 1, c1 = c2, c0;
                    while (c1 > 0 && recList[c1].speed < 0 == partReverse
                        && glm.Distance(recList[c2].easting, recList[c2].northing, recList[c1].easting, recList[c1].northing) < 1.0) c1--;
                    c0 = c1;
                    while (c0 > 0 && recList[c0].speed < 0 == partReverse
                        && glm.Distance(recList[c1].easting, recList[c1].northing, recList[c0].easting, recList[c0].northing) < 1.0) c0--;
                    double h2 = Math.Atan2(recList[c2].northing - recList[c1].northing, recList[c2].easting - recList[c1].easting);
                    double h1 = Math.Atan2(recList[c1].northing - recList[c0].northing, recList[c1].easting - recList[c0].easting);
                    double len21 = glm.Distance(recList[c2].easting, recList[c2].northing, recList[c1].easting, recList[c1].northing);
                    double len10 = glm.Distance(recList[c1].easting, recList[c1].northing, recList[c0].easting, recList[c0].northing);
                    double kappa = 0;
                    if (len21 > 0.3 && len10 > 0.3)
                    {
                        double dh = h2 - h1;
                        while (dh > Math.PI) dh -= glm.twoPI;
                        while (dh < -Math.PI) dh += glm.twoPI;
                        kappa = dh / ((len21 + len10) * 0.5);
                        // the chord direction lags the tangent at the cusp by half the turn of the chord
                        h2 += kappa * len21 * 0.5;
                    }
                    if (len21 < 0.05) break;
                    double rem = goalPointDistance - distSoFar - Math.Sqrt(((start.easting - recList[c2].easting) * (start.easting - recList[c2].easting))
                        + ((start.northing - recList[c2].northing) * (start.northing - recList[c2].northing)));
                    if (rem < 0) rem = 0;
                    if (Math.Abs(kappa) < 1e-4)
                    {
                        goalPointRP.easting = recList[c2].easting + (Math.Cos(h2) * rem);
                        goalPointRP.northing = recList[c2].northing + (Math.Sin(h2) * rem);
                    }
                    else
                    {
                        goalPointRP.easting = recList[c2].easting + ((Math.Sin(h2 + (kappa * rem)) - Math.Sin(h2)) / kappa);
                        goalPointRP.northing = recList[c2].northing - ((Math.Cos(h2 + (kappa * rem)) - Math.Cos(h2)) / kappa);
                    }
                    goalFound = true;
                    break;
                }
                lastIdx = i;
                // used for calculating the length squared of next segment.
                double tempDist = Math.Sqrt((start.easting - recList[i].easting) * (start.easting - recList[i].easting)
                    + (start.northing - recList[i].northing) * (start.northing - recList[i].northing));

                //will we go too far?
                if ((tempDist + distSoFar) > goalPointDistance)
                {
                    double j = (goalPointDistance - distSoFar) / tempDist; // the remainder to yet travel

                    goalPointRP.easting = (((1 - j) * start.easting) + (j * recList[i].easting));
                    goalPointRP.northing = (((1 - j) * start.northing) + (j * recList[i].northing));
                    goalFound = true;
                    break;
                }
                else distSoFar += tempDist;
                start = recList[i];
            }

            // the path ends before the look-ahead distance (near the end of a turn, nothing straight
            // ahead of the nose): the goal is the last point of the path, not the old goal point,
            // which kept the wheels turned the wrong way until the end of the turn
            if (!goalFound && lastIdx >= 0)
            {
                goalPointRP.easting = recList[lastIdx].easting;
                goalPointRP.northing = recList[lastIdx].northing;
            }

            //calc "D" the distance from pivotAxlePosRP axle to lookahead point
            double goalPointDistanceSquared = glm.DistanceSquared(goalPointRP.northing, goalPointRP.easting, pivotAxlePosRP.northing, pivotAxlePosRP.easting);

            //calculate the the delta x in local coordinates and steering angle degrees based on wheelbase
            // driving in reverse: the steering axle is the trailing one, so the geometry is taken from
            // the direction of motion (nose heading + 180 degrees) and the steering angle is mirrored
            bool reverseDrive = mf.isReverse;
            double headingRP = reverseDrive ? mf.fixHeading + Math.PI : mf.fixHeading;
            double localHeading = glm.twoPI - headingRP + inty;

            ppRadiusRP = goalPointDistanceSquared / (2 * (((goalPointRP.easting - pivotAxlePosRP.easting) * Math.Cos(localHeading)) + ((goalPointRP.northing - pivotAxlePosRP.northing) * Math.Sin(localHeading))));

            steerAngleRP = glm.toDegrees(Math.Atan(2 * (((goalPointRP.easting - pivotAxlePosRP.easting) * Math.Cos(localHeading))
                + ((goalPointRP.northing - pivotAxlePosRP.northing) * Math.Sin(localHeading))) * mf.vehicle.VehicleConfig.Wheelbase / goalPointDistanceSquared));
            if (reverseDrive) steerAngleRP = -steerAngleRP;

            if (steerAngleRP < -mf.vehicle.maxSteerAngle) steerAngleRP = -mf.vehicle.maxSteerAngle;
            if (steerAngleRP > mf.vehicle.maxSteerAngle) steerAngleRP = mf.vehicle.maxSteerAngle;

            //used for smooth mode
            mf.vehicle.modeActualXTE = (distanceFromCurrentLinePivot);

            //Convert to centimeters
            mf.guidanceLineDistanceOff = (short)Math.Round(distanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);
            mf.guidanceLineSteerAngle = (short)(steerAngleRP * 100);
        }

        private void PurePursuitDubins(int ptCount)
        {
            double dist, dx, dz;
            double minDistA = 1000000, minDistB = 1000000;

            //find the closest 2 points to current fix
            for (int t = 0; t < ptCount; t++)
            {
                dist = ((pivotAxlePosRP.easting - shuttleDubinsList[t].easting) * (pivotAxlePosRP.easting - shuttleDubinsList[t].easting))
                                + ((pivotAxlePosRP.northing - shuttleDubinsList[t].northing) * (pivotAxlePosRP.northing - shuttleDubinsList[t].northing));
                if (dist < minDistA)
                {
                    minDistB = minDistA;
                    B = A;
                    minDistA = dist;
                    A = t;
                }
                else if (dist < minDistB)
                {
                    minDistB = dist;
                    B = t;
                }
            }

            //just need to make sure the points continue ascending or heading switches all over the place
            if (A > B) { C = A; A = B; B = C; }

            //currentLocationIndex = A;

            //get the distance from currently active AB line
            dx = shuttleDubinsList[B].easting - shuttleDubinsList[A].easting;
            dz = shuttleDubinsList[B].northing - shuttleDubinsList[A].northing;

            if (Math.Abs(dx) < Double.Epsilon && Math.Abs(dz) < Double.Epsilon) return;

            //abHeading = Math.Atan2(dz, dx);

            //how far from current AB Line is fix
            distanceFromCurrentLinePivot = ((dz * pivotAxlePosRP.easting) - (dx * pivotAxlePosRP.northing) + (shuttleDubinsList[B].easting
                        * shuttleDubinsList[A].northing) - (shuttleDubinsList[B].northing * shuttleDubinsList[A].easting))
                            / Math.Sqrt((dz * dz) + (dx * dx));

            //integral slider is set to 0
            if (mf.vehicle.purePursuitIntegralGain != 0)
            {
                pivotDistanceError = distanceFromCurrentLinePivot * 0.2 + pivotDistanceError * 0.8;

                if (counter2++ > 4)
                {
                    pivotDerivative = pivotDistanceError - pivotDistanceErrorLast;
                    pivotDistanceErrorLast = pivotDistanceError;
                    counter2 = 0;
                    pivotDerivative *= 2;

                    //limit the derivative
                    //if (pivotDerivative > 0.03) pivotDerivative = 0.03;
                    //if (pivotDerivative < -0.03) pivotDerivative = -0.03;
                    //if (Math.Abs(pivotDerivative) < 0.01) pivotDerivative = 0;
                }

                //pivotErrorTotal = pivotDistanceError + pivotDerivative;

                if (mf.isBtnAutoSteerOn
                    && Math.Abs(pivotDerivative) < (0.1)
                    && mf.avgSpeed > 2.5
                    && !mf.yt.isYouTurnTriggered)

                {
                    //if over the line heading wrong way, rapidly decrease integral
                    if ((inty < 0 && distanceFromCurrentLinePivot < 0) || (inty > 0 && distanceFromCurrentLinePivot > 0))
                    {
                        inty += pivotDistanceError * mf.vehicle.purePursuitIntegralGain * -0.04;
                    }
                    else
                    {
                        if (Math.Abs(distanceFromCurrentLinePivot) > 0.02)
                        {
                            inty += pivotDistanceError * mf.vehicle.purePursuitIntegralGain * -0.02;
                            if (inty > 0.2) inty = 0.2;
                            else if (inty < -0.2) inty = -0.2;
                        }
                    }
                }
                else inty *= 0.95;
            }
            else inty = 0;

            if (mf.isReverse) inty = 0;

            // ** Pure pursuit ** - calc point on ABLine closest to current position
            double U = (((pivotAxlePosRP.easting - shuttleDubinsList[A].easting) * dx)
                        + ((pivotAxlePosRP.northing - shuttleDubinsList[A].northing) * dz))
                        / ((dx * dx) + (dz * dz));

            rEastRP = shuttleDubinsList[A].easting + (U * dx);
            rNorthRP = shuttleDubinsList[A].northing + (U * dz);

            //update base on autosteer settings and distance from line
            double goalPointDistance = mf.vehicle.UpdateGoalPointDistance();

            bool ReverseHeading = !mf.isReverse;

            int count = ReverseHeading ? 1 : -1;
            CRecPathPt start = new CRecPathPt(rEastRP, rNorthRP, 0, 0, false);
            double distSoFar = 0;

            for (int i = ReverseHeading ? B : A; i < ptCount && i >= 0; i += count)
            {
                // used for calculating the length squared of next segment.
                double tempDist = Math.Sqrt((start.easting - shuttleDubinsList[i].easting) * (start.easting - shuttleDubinsList[i].easting)
                    + (start.northing - shuttleDubinsList[i].northing) * (start.northing - shuttleDubinsList[i].northing));

                //will we go too far?
                if ((tempDist + distSoFar) > goalPointDistance)
                {
                    double j = (goalPointDistance - distSoFar) / tempDist; // the remainder to yet travel

                    goalPointRP.easting = (((1 - j) * start.easting) + (j * shuttleDubinsList[i].easting));
                    goalPointRP.northing = (((1 - j) * start.northing) + (j * shuttleDubinsList[i].northing));
                    break;
                }
                else distSoFar += tempDist;
                start = shuttleDubinsList[i];
            }

            //calc "D" the distance from pivotAxlePosRP axle to lookahead point
            double goalPointDistanceSquared = glm.DistanceSquared(goalPointRP.northing, goalPointRP.easting, pivotAxlePosRP.northing, pivotAxlePosRP.easting);

            //calculate the the delta x in local coordinates and steering angle degrees based on wheelbase
            //double localHeading = glm.twoPI - mf.fixHeading;

            double localHeading = glm.twoPI - mf.fixHeading + inty;

            ppRadiusRP = goalPointDistanceSquared / (2 * (((goalPointRP.easting - pivotAxlePosRP.easting) * Math.Cos(localHeading)) + ((goalPointRP.northing - pivotAxlePosRP.northing) * Math.Sin(localHeading))));

            steerAngleRP = glm.toDegrees(Math.Atan(2 * (((goalPointRP.easting - pivotAxlePosRP.easting) * Math.Cos(localHeading))
                + ((goalPointRP.northing - pivotAxlePosRP.northing) * Math.Sin(localHeading))) * mf.vehicle.VehicleConfig.Wheelbase / goalPointDistanceSquared));

            if (steerAngleRP < -mf.vehicle.maxSteerAngle) steerAngleRP = -mf.vehicle.maxSteerAngle;
            if (steerAngleRP > mf.vehicle.maxSteerAngle) steerAngleRP = mf.vehicle.maxSteerAngle;

            if (ppRadiusRP < -500) ppRadiusRP = -500;
            if (ppRadiusRP > 500) ppRadiusRP = 500;

            radiusPointRP.easting = pivotAxlePosRP.easting + (ppRadiusRP * Math.Cos(localHeading));
            radiusPointRP.northing = pivotAxlePosRP.northing + (ppRadiusRP * Math.Sin(localHeading));

            //angular velocity in rads/sec  = 2PI * m/sec * radians/meters
            // double angVel = glm.twoPI * 0.277777 * mf.pn.speed * (Math.Tan(glm.toRadians(steerAngleRP))) / mf.vehicle.wheelbase;

            //clamp the steering angle to not exceed safe angular velocity
            //if (Math.Abs(angVel) > mf.vehicle.maxAngularVelocity)
            //{
            //    steerAngleRP = glm.toDegrees(steerAngleRP > 0 ?
            //            (Math.Atan((mf.vehicle.wheelbase * mf.vehicle.maxAngularVelocity) / (glm.twoPI * mf.avgSpeed * 0.277777)))
            //        : (Math.Atan((mf.vehicle.wheelbase * -mf.vehicle.maxAngularVelocity) / (glm.twoPI * mf.avgSpeed * 0.277777))));
            //}

            //Convert to centimeters
            mf.guidanceLineDistanceOff = (short)Math.Round(distanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);
            mf.guidanceLineSteerAngle = (short)(steerAngleRP * 100);
        }

        // A working run that ends where it started is a headland lap
        private bool IsClosedLoop(int a, int b)
        {
            if (b - a < 3) return false;
            double len = 0;
            for (int k = a + 1; k <= b; k++)
                len += glm.Distance(recList[k - 1].easting, recList[k - 1].northing, recList[k].easting, recList[k].northing);
            double gap = glm.Distance(recList[a].easting, recList[a].northing, recList[b].easting, recList[b].northing);
            return len > 50.0 && gap < len * 0.05;
        }

        // Hue (0..360, red = warm) to RGB, full saturation and brightness
        private static void HsvToRgb(double hue, out float r, out float g, out float b)
        {
            double h = (hue % 360) / 60.0;
            int k = (int)Math.Floor(h);
            double f = h - k;
            double q = 1 - f;
            switch (k)
            {
                case 0: r = 1; g = (float)f; b = 0; break;
                case 1: r = (float)q; g = 1; b = 0; break;
                case 2: r = 0; g = 1; b = (float)f; break;
                case 3: r = 0; g = (float)q; b = 1; break;
                case 4: r = (float)f; g = 0; b = 1; break;
                default: r = 1; g = 0; b = (float)q; break;
            }
        }

        public void DrawRecordedLine()
        {
            int ptCount = recList.Count;
            if (ptCount < 1) return;
            // Every run of the path is grey, except the active pass (yellow) and the pass after it
            // (white). Headland laps and turns are grey too.
            var runs = new List<(int from, int end, bool work, bool headland)>();
            int i = 0;
            while (i < ptCount)
            {
                bool work = recList[i].autoBtnState;
                int runStart = i;
                while (i < ptCount && recList[i].autoBtnState == work) i++;
                int runEnd = i - 1;
                bool headland = work && (IsClosedLoop(runStart, runEnd) || Math.Abs(recList[runStart].speed - FormGPS.AutoPlanHeadlandSpeed) < 0.05);
                runs.Add((Math.Max(0, runStart - 1), runEnd, work, headland));
            }

            int cur = Math.Max(0, Math.Min(ptCount - 1, currentPositonIndex));
            int activeRun = -1, nextRun = -1;
            for (int r = 0; r < runs.Count; r++)
            {
                if (!runs[r].work || runs[r].headland) continue;
                if (activeRun < 0 && cur <= runs[r].end) { activeRun = r; continue; }
                if (activeRun >= 0) { nextRun = r; break; }
            }

            // the turn between the active pass and the next one is light blue
            int turnRun = -1;
            if (activeRun >= 0 && nextRun >= 0)
                for (int r = activeRun + 1; r < nextRun; r++) if (!runs[r].work) turnRun = r;

            // turns coloured by their order along the path
            int turnNo = 0;

            for (int r = 0; r < runs.Count; r++)
            {
                var run = runs[r];
                if (r == activeRun) { GL.LineWidth(4); GL.Color3(0.98f, 0.92f, 0.46f); }
                else if (r == nextRun) { GL.LineWidth(4); GL.Color3(1.0f, 1.0f, 1.0f); }
                else if (r == turnRun) { GL.LineWidth(3); GL.Color3(0.60f, 0.90f, 1.0f); }
                else if (!run.work)
                {
                    // every turn its own colour: hues spaced by the golden angle, so neighbours differ
                    // and the colours do not repeat soon; yellow and light blue are skipped
                    double hue = (turnNo * 137.508) % 360.0;
                    if (hue > 40 && hue < 75) hue += 40;
                    if (hue > 185 && hue < 215) hue += 30;
                    turnNo++;
                    HsvToRgb(hue, out float rr, out float gg, out float bb);
                    GL.LineWidth(2.5f); GL.Color3(rr, gg, bb);
                }
                else { GL.LineWidth(1.5f); GL.Color3(0.55f, 0.57f, 0.60f); }

                GL.Begin(PrimitiveType.LineStrip);
                for (int h = run.from; h <= run.end; h++) GL.Vertex3(recList[h].easting, recList[h].northing, 0);
                GL.End();
            }

            // reverse parts of turns: dashed, light grey
            GL.LineWidth(3);
            GL.Color3(0.80f, 0.80f, 0.82f);
            GL.Enable(EnableCap.LineStipple);
            GL.LineStipple(1, 0x0F0F);
            GL.Begin(PrimitiveType.Lines);
            for (int h = 1; h < ptCount; h++)
            {
                if (recList[h].speed >= 0) continue;
                GL.Vertex3(recList[h - 1].easting, recList[h - 1].northing, 0);
                GL.Vertex3(recList[h].easting, recList[h].northing, 0);
            }
            GL.End();
            GL.Disable(EnableCap.LineStipple);
            GL.LineWidth(1);

            if (!isRecordOn)
            {
                //Draw lookahead Point
                GL.PointSize(16.0f);
                GL.Begin(PrimitiveType.Points);

                //GL.Color(1.0f, 1.0f, 0.25f);
                //GL.Vertex(rEast, rNorth, 0.0);

                GL.Color3(1.0f, 0.5f, 0.95f);
                GL.Vertex3(recList[currentPositonIndex].easting, recList[currentPositonIndex].northing, 0);
                GL.End();
                GL.PointSize(1.0f);
            }
        }

        public void DrawDubins()
        {
            if (shuttleDubinsList.Count > 1)
            {
                //GL.LineWidth(2);
                GL.PointSize(2);
                GL.Color3(0.298f, 0.96f, 0.2960f);
                GL.Begin(PrimitiveType.Points);
                for (int h = 0; h < shuttleDubinsList.Count; h++)
                    GL.Vertex3(shuttleDubinsList[h].easting, shuttleDubinsList[h].northing, 0);
                GL.End();
            }
        }
    }
}
